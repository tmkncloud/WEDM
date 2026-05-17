using System.Collections.Concurrent;

namespace WEDM.Infrastructure.Persistence;

/// <summary>
/// Corruption-safe atomic file writer with bounded, self-cleaning per-path lock coordination.
///
/// R-04 fix: replaces the previous unbounded <c>static Dictionary{string, SemaphoreSlim}</c>
/// (which caused permanent handle leaks in long-running sessions) with a reference-counted
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> of <see cref="PathLockEntry"/> objects.
///
/// Lock lifecycle:
///   Acquire  — increment reference count; create entry if absent.
///   Release  — decrement reference count; mark entry eligible for eviction.
///   Cleanup  — runs opportunistically on a background thread; disposes semaphores
///              only when reference count is zero and the entry has been idle for
///              at least <see cref="IdleEvictionMs"/> milliseconds.
///              Forced cleanup via <see cref="Trim"/> bypasses the idle-time gate.
///
/// Concurrency guarantees:
///   • No two concurrent writers for the same path execute their I/O simultaneously.
///   • A semaphore is NEVER disposed while any thread holds a reference (ref count > 0).
///     This is enforced by a double-check protocol on <see cref="PathLockEntry._refCount"/>
///     and <see cref="PathLockEntry._disposedInt"/>, both accessed only via
///     <see cref="Interlocked"/> or <see cref="Volatile"/> operations.
///   • If a cleanup pass disposes an entry between AcquireEntry's TryGetValue and
///     its Increment call, the IsDisposed guard catches the race and retries.
///   • Cleanup never deadlocks — it is lock-free and yields on any conflict.
///   • Safe for parallel deployments, report generation, rollback operations,
///     checkpoint persistence, and diagnostics export running concurrently.
/// </summary>
public static class AtomicFileWriter
{
    // ── Tuning constants ──────────────────────────────────────────────────────

    /// <summary>
    /// An idle lock entry (ref count = 0) is eligible for eviction after this
    /// many milliseconds of inactivity.  Keeps recently-used entries warm to
    /// avoid constant create/destroy churn on repeated writes to the same path.
    /// </summary>
    internal const int IdleEvictionMs = 30_000;   // 30 s

    /// <summary>
    /// Opportunistic background cleanup is triggered when the dictionary
    /// reaches this many entries.  Acts as a growth ceiling between explicit
    /// <see cref="Trim"/> calls.
    /// </summary>
    internal const int CleanupThreshold = 200;

    // ── Lock registry ─────────────────────────────────────────────────────────

    /// <summary>
    /// Path-keyed, case-insensitive lock entry registry.
    /// Exposed as internal for test inspection.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, PathLockEntry> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guards entry creation under contention so that exactly one new
    /// <see cref="PathLockEntry"/> reaches the dictionary per path per miss.
    /// Held for microseconds only — never during I/O.
    /// </summary>
    private static readonly object CreationLock = new();

    /// <summary>0 or 1 — prevents overlapping background cleanup passes.</summary>
    private static int _cleanupInFlight;

    // ── Diagnostics counters ──────────────────────────────────────────────────

    private static long _evictedCount;
    private static long _disposedCount;
    private static long _cleanupPassCount;
    private static long _peakLockUsage;

    // ── Public write API ──────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="content"/> atomically to <paramref name="targetPath"/>
    /// via a temp-file swap.  Concurrent calls for the same path are serialised;
    /// different paths proceed independently.
    /// </summary>
    public static async Task WriteAllTextAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken = default)
    {
        var entry = AcquireEntry(targetPath);
        await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteTextCoreAsync(targetPath, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.Semaphore.Release();
            ReleaseEntry(targetPath, entry);
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> bytes atomically to <paramref name="targetPath"/>
    /// via a temp-file swap.  Concurrent calls for the same path are serialised.
    /// </summary>
    public static async Task WriteAllBytesAsync(
        string targetPath,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var entry = AcquireEntry(targetPath);
        await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteBytesCoreAsync(targetPath, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.Semaphore.Release();
            ReleaseEntry(targetPath, entry);
        }
    }

    /// <summary>
    /// Proactively evicts ALL idle lock entries regardless of the idle-time gate.
    /// Call after a deployment session ends to reclaim handles accumulated during
    /// that session and prepare for the next.
    /// </summary>
    public static void Trim() => RunCleanup(force: true);

    /// <summary>Returns a snapshot of the lock registry's operational diagnostics.</summary>
    public static AtomicFileWriterDiagnostics GetDiagnostics() => new()
    {
        ActiveLockCount        = PathLocks.Count,
        EvictedLockCount       = Interlocked.Read(ref _evictedCount),
        DisposedSemaphoreCount = Interlocked.Read(ref _disposedCount),
        CleanupPassCount       = Interlocked.Read(ref _cleanupPassCount),
        PeakLockUsage          = Interlocked.Read(ref _peakLockUsage),
    };

    // ── Core I/O helpers (unchanged semantics from original) ──────────────────

    private static async Task WriteTextCoreAsync(
        string path, string content, CancellationToken ct)
    {
        EnsureDirectory(path);
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, ct).ConfigureAwait(false);
            AtomicSwap(path, tempPath);
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }
    }

    private static async Task WriteBytesCoreAsync(
        string path, byte[] content, CancellationToken ct)
    {
        EnsureDirectory(path);
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, content, ct).ConfigureAwait(false);
            AtomicSwap(path, tempPath);
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }
    }

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private static void AtomicSwap(string target, string temp)
    {
        if (File.Exists(target))
            File.Replace(temp, target,
                destinationBackupFileName: target + ".bak",
                ignoreMetadataErrors: true);
        else
            File.Move(temp, target);
    }

    private static void TryDeleteTemp(string tempPath)
    {
        if (File.Exists(tempPath))
            try { File.Delete(tempPath); } catch { /* best effort */ }
    }

    // ── Lock lifecycle ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a live <see cref="PathLockEntry"/> for <paramref name="path"/>
    /// with its reference count already incremented.
    ///
    /// Retry loop handles the disposal race:
    ///   If a cleanup pass disposed the entry between our retrieval and our
    ///   Increment call, <see cref="PathLockEntry.IsDisposed"/> will be true
    ///   after the increment.  We undo the increment, remove the stale entry
    ///   from the dictionary, and retry — which will create a fresh entry.
    /// </summary>
    private static PathLockEntry AcquireEntry(string path)
    {
        while (true)
        {
            // ── Fast path: existing live entry ────────────────────────────────
            if (PathLocks.TryGetValue(path, out var existing))
            {
                // Increment BEFORE the disposal check.  This creates a
                // sequentially-consistent ordering with the cleanup thread's
                // double-check (see RunCleanup), ensuring one side or the
                // other always wins cleanly.
                Interlocked.Increment(ref existing._refCount);

                if (!existing.IsDisposed)
                {
                    existing.TouchLastUsed();
                    TrackPeak();
                    return existing;
                }

                // We raced with disposal — undo and fall through to creation.
                Interlocked.Decrement(ref existing._refCount);

                // Remove the disposed entry so the creation path sees a clean slot.
                // Uses the value-checked overload: only removes if the value is
                // still this specific disposed entry (prevents removing a fresh
                // entry that another thread may have already installed).
                PathLocks.TryRemove(new KeyValuePair<string, PathLockEntry>(path, existing));
            }

            // ── Slow path: create a fresh entry ───────────────────────────────
            lock (CreationLock)
            {
                // Double-check: another thread may have created a live entry
                // between the failed TryGetValue above and acquiring this lock.
                if (PathLocks.TryGetValue(path, out existing) && !existing.IsDisposed)
                {
                    Interlocked.Increment(ref existing._refCount);
                    existing.TouchLastUsed();
                    TrackPeak();
                    return existing;
                }

                var fresh = new PathLockEntry();
                Interlocked.Increment(ref fresh._refCount);
                PathLocks[path] = fresh;
                TrackPeak();
                return fresh;
            }
        }
    }

    private static void ReleaseEntry(string path, PathLockEntry entry)
    {
        entry.TouchLastUsed();
        Interlocked.Decrement(ref entry._refCount);

        // Opportunistic cleanup when the table is growing large.
        if (PathLocks.Count >= CleanupThreshold)
            TriggerBackgroundCleanup();
    }

    private static void TrackPeak()
    {
        // Lock-free high-water mark via CAS loop.
        long current = PathLocks.Count;
        long observed;
        do
        {
            observed = Interlocked.Read(ref _peakLockUsage);
            if (current <= observed) return;
        }
        while (Interlocked.CompareExchange(ref _peakLockUsage, current, observed) != observed);
    }

    // ── Cleanup / eviction ────────────────────────────────────────────────────

    private static void TriggerBackgroundCleanup()
    {
        // Only one cleanup pass at a time — cheap guard.
        if (Interlocked.CompareExchange(ref _cleanupInFlight, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try  { RunCleanup(force: false); }
            finally { Interlocked.Exchange(ref _cleanupInFlight, 0); }
        });
    }

    /// <summary>
    /// Evicts idle entries from the dictionary and disposes their semaphores.
    ///
    /// Disposal protocol (ensures no semaphore is disposed while in use):
    ///   1. Skip entries whose ref count is > 0 (actively in use).
    ///   2. Win the disposal right via CAS on <see cref="PathLockEntry._disposedInt"/>
    ///      (0 → 1).  Only one cleanup thread can win per entry.
    ///   3. Re-check ref count AFTER CAS.  If a concurrent AcquireEntry incremented
    ///      the ref count between our check (step 1) and our CAS (step 2), we must
    ///      NOT dispose — restore the flag and skip.
    ///   4. Remove from the dictionary and dispose the semaphore.
    ///
    /// This protocol is sequentially consistent: the Interlocked operations on
    /// _refCount and _disposedInt impose a total order that prevents any thread
    /// from observing a disposed-but-in-use semaphore.
    /// </summary>
    internal static void RunCleanup(bool force)
    {
        Interlocked.Increment(ref _cleanupPassCount);

        // Entries idle before this threshold are eviction candidates.
        var cutoffTicks = DateTime.UtcNow.AddMilliseconds(-IdleEvictionMs).Ticks;

        foreach (var (path, entry) in PathLocks)
        {
            // Skip already-disposed entries (concurrent cleanup may have won).
            if (entry.IsDisposed) continue;

            // Skip any entry still in use.
            if (Volatile.Read(ref entry._refCount) > 0) continue;

            // Skip entries that are still within their idle grace period,
            // unless we're doing a forced trim.
            if (!force && entry.LastUsedTicks > cutoffTicks) continue;

            // ── Win the disposal right (CAS 0 → 1) ──────────────────────────
            if (Interlocked.CompareExchange(ref entry._disposedInt, 1, 0) != 0)
                continue;  // another cleanup thread won this entry

            // ── Double-check: did AcquireEntry race in? ──────────────────────
            // AcquireEntry does: Increment(_refCount) → check IsDisposed.
            // We just set IsDisposed=1.  If AcquireEntry's Increment happened
            // before our CAS, AcquireEntry will see IsDisposed=true and undo
            // its increment, leaving refCount at 0 again — safe to dispose.
            // If AcquireEntry's Increment happened after our CAS, we will see
            // refCount > 0 here and must NOT dispose.
            if (Volatile.Read(ref entry._refCount) > 0)
            {
                // AcquireEntry beat us — restore the flag and leave the entry live.
                // The increment by AcquireEntry guarantees another Release will
                // eventually decrement refCount, making this entry eligible again.
                Interlocked.Exchange(ref entry._disposedInt, 0);
                continue;
            }

            // ── Safe to evict: ref count is 0 and we own the disposal right. ─
            // TryRemove with the value check prevents removing a fresh entry
            // that a concurrent AcquireEntry may have installed over this one.
            PathLocks.TryRemove(new KeyValuePair<string, PathLockEntry>(path, entry));
            entry.Semaphore.Dispose();
            Interlocked.Increment(ref _evictedCount);
            Interlocked.Increment(ref _disposedCount);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// PathLockEntry — reference-counted per-path lock
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A ref-counted, evictable wrapper around a <see cref="SemaphoreSlim"/>.
///
/// Field access rules:
///   <c>_refCount</c>    — read/write exclusively via <see cref="Interlocked"/>.
///   <c>_disposedInt</c> — CAS-guarded; read via <see cref="Volatile.Read"/>.
///   <c>_lastUsedTicks</c>  — read/write via <see cref="Volatile"/>; used only
///                         for eviction heuristics, so torn reads are harmless.
///
/// Marked internal for unit test inspection.
/// </summary>
internal sealed class PathLockEntry
{
    /// <summary>The mutex protecting one file path.  Disposed on eviction.</summary>
    public readonly SemaphoreSlim Semaphore = new(1, 1);

    /// <summary>
    /// Active reference count.  Zero means no thread currently holds this entry.
    /// Access only via <see cref="Interlocked.Increment"/>, <see cref="Interlocked.Decrement"/>,
    /// and <see cref="Interlocked.Read"/>.
    /// </summary>
    public int _refCount;

    /// <summary>
    /// Disposal state: 0 = live, 1 = disposed (or being disposed).
    /// Modified only via <see cref="Interlocked.CompareExchange"/> and
    /// <see cref="Interlocked.Exchange"/>; read via <see cref="Volatile.Read"/>.
    /// </summary>
    public int _disposedInt;

    private long _lastUsedTicks;

    public PathLockEntry()
    {
        Volatile.Write(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>True when this entry has been marked for disposal or already disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposedInt) != 0;

    /// <summary>Timestamp of the last acquire or release, as UTC ticks.</summary>
    public long LastUsedTicks => Volatile.Read(ref _lastUsedTicks);

    /// <summary>Records the current time as the last-used timestamp.</summary>
    public void TouchLastUsed()
        => Volatile.Write(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
}

// ═══════════════════════════════════════════════════════════════════════════════
// AtomicFileWriterDiagnostics — operational snapshot
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Point-in-time diagnostic snapshot of the <see cref="AtomicFileWriter"/> lock registry.
/// All values are eventual-consistent approximations suitable for telemetry and logging.
/// </summary>
public sealed class AtomicFileWriterDiagnostics
{
    /// <summary>
    /// Current number of paths tracked in the registry.
    /// Includes both in-use and idle-but-not-yet-evicted entries.
    /// </summary>
    public int ActiveLockCount { get; init; }

    /// <summary>
    /// Cumulative number of lock entries removed from the registry since process start.
    /// </summary>
    public long EvictedLockCount { get; init; }

    /// <summary>
    /// Cumulative number of <see cref="SemaphoreSlim"/> instances disposed since process start.
    /// Should equal <see cref="EvictedLockCount"/> under normal operation.
    /// </summary>
    public long DisposedSemaphoreCount { get; init; }

    /// <summary>
    /// Total cleanup passes (both opportunistic and forced) since process start.
    /// </summary>
    public long CleanupPassCount { get; init; }

    /// <summary>
    /// Highest concurrent path lock count observed since process start (high-water mark).
    /// Useful for capacity planning and detecting lock accumulation trends.
    /// </summary>
    public long PeakLockUsage { get; init; }

    public override string ToString()
        => $"Active={ActiveLockCount}, Peak={PeakLockUsage}, Evicted={EvictedLockCount}, " +
           $"Disposed={DisposedSemaphoreCount}, CleanupPasses={CleanupPassCount}";
}
