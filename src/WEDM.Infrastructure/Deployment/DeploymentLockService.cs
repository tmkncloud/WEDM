using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Deployment;

/// <summary>
/// Machine-wide mutex for Oracle Home, domain, and central inventory paths.
///
/// ── Lock file protocol (v2) ───────────────────────────────────────────────
///
/// A single <c>{lockRoot}/{sanitizedKey}.meta</c> file represents a held lock.
/// Its existence is the lock; its JSON content is the metadata.
///
/// Acquisition (atomic exclusive):
///   1. Write descriptor JSON to <c>{sanitizedKey}-{sessionId:N}.meta.tmp</c>
///      (provisional file, fully flushed to disk before rename).
///   2. <c>File.Move(provisional, meta, overwrite: false)</c>
///      — atomic rename; throws IOException if <c>.meta</c> already exists.
///      Exactly one concurrent caller wins this rename: that caller owns the lock.
///   3. On success: lock acquired, provisional is now the live .meta file.
///   4. On IOException: another holder won; clean up provisional, read winner's .meta.
///
/// Release: delete <c>.meta</c>.
///
/// ── Crash safety ─────────────────────────────────────────────────────────
///
/// Crash before step 1  → nothing written, no orphan.
/// Crash during step 1  → provisional may be partial; <see cref="ReconcileAsync"/>
///                         removes orphaned provisionals older than 10 minutes.
/// Crash between 1 and 2→ provisional exists, no .meta; same reconciliation handles it.
/// Crash during step 2  → OS-level atomic rename: either completes or reverts.
///                         Provisional may linger but .meta is either valid or absent.
///
/// ── Backward compatibility (v1 legacy locks) ─────────────────────────────
///
/// Pre-R02 code wrote an empty <c>.lock</c> sentinel + a <c>.meta</c> data file.
/// <see cref="ReconcileAsync"/> removes orphaned <c>.lock</c> files that have no
/// valid <c>.meta</c> partner, and keeps valid v1 <c>.meta</c> files as-is.
///
/// ── Intra-process race prevention ────────────────────────────────────────
///
/// A per-resource-key <see cref="SemaphoreSlim"/> serialises concurrent acquisition
/// attempts within the same process.  Cross-process exclusion is provided by the
/// atomic File.Move gate.  The <c>_resourceGates</c> dictionary is bounded to
/// the small fixed set of resource keys per deployment (typically 3: mw/inv/dom),
/// so it does not exhibit the unbounded-growth issue noted in R-04.
/// </summary>
public sealed class DeploymentLockService : IDeploymentLockService
{
    // ── Constants ─────────────────────────────────────────────────────────────

    public static readonly TimeSpan DefaultStaleLockAge         = TimeSpan.FromHours(4);
    public static readonly TimeSpan OrphanedProvisionalMaxAge   = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan CrossMachineAbandonMaxAge   = TimeSpan.FromHours(8);

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly string _lockRoot;

    /// <summary>
    /// Per-resource-key semaphore for intra-process serialisation.
    /// Not used for cross-process exclusion (File.Move handles that).
    /// Keys are bounded: at most one entry per Oracle resource (3 per deployment config).
    /// </summary>
    private readonly Dictionary<string, SemaphoreSlim> _resourceGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gatesLock = new();

    // ── Construction ──────────────────────────────────────────────────────────

    public DeploymentLockService(string? lockRoot = null)
    {
        _lockRoot = lockRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WEDM", "locks");
        Directory.CreateDirectory(_lockRoot);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Acquisition
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<DeploymentLockAcquireResult> TryAcquireAsync(
        DeploymentConfiguration config,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        // Cleanup before acquiring so stale locks don't block fresh deployments.
        await CleanupStaleLocksAsync(DefaultStaleLockAge, cancellationToken).ConfigureAwait(false);

        var resources = BuildResourceList(config);
        var acquired  = new List<DeploymentLockDescriptor>();
        var conflicts = new List<DeploymentLockDescriptor>();

        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Serialise intra-process attempts for this resource key.
            var gate = GetResourceGate(resource.Key);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (descriptor, conflict) = await TryAcquireOneResourceAsync(
                    resource, config, sessionId, cancellationToken).ConfigureAwait(false);

                if (descriptor is not null)
                    acquired.Add(descriptor);
                else if (conflict is not null)
                    conflicts.Add(conflict);
            }
            finally
            {
                gate.Release();
            }
        }

        // If any resource conflicted, release everything we acquired (atomic all-or-nothing).
        if (conflicts.Count > 0)
        {
            foreach (var d in acquired)
                TryDeleteMeta(MetaFilePath(d.ResourceKey));

            // Clean up any orphaned provisionals we may have left for this session.
            CleanupSessionProvisionals(sessionId);

            return new DeploymentLockAcquireResult
            {
                Acquired         = false,
                FailureReason    = BuildConflictMessage(conflicts),
                ConflictingLocks = conflicts
            };
        }

        return new DeploymentLockAcquireResult
        {
            Acquired      = true,
            AcquiredLocks = acquired
        };
    }

    /// <summary>
    /// Attempt to acquire a single resource lock.
    /// Returns (descriptor, null) on success or (null, conflict) on conflict.
    /// </summary>
    private async Task<(DeploymentLockDescriptor? descriptor, DeploymentLockDescriptor? conflict)>
        TryAcquireOneResourceAsync(
            (string Key, string Type, string Path) resource,
            DeploymentConfiguration config,
            Guid sessionId,
            CancellationToken cancellationToken)
    {
        var metaPath        = MetaFilePath(resource.Key);
        var provisionalPath = ProvisionalFilePath(resource.Key, sessionId);

        // ── Pre-check: is the resource already locked? ─────────────────────
        var existing = await TryReadMetaAsync(metaPath, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.SessionId != sessionId)
        {
            // Check whether the existing lock is stale before reporting conflict.
            var stale = existing.IsStale(DefaultStaleLockAge)
                        && !IsProcessAlive(existing.OwnerProcessId);
            if (!stale)
                return (null, existing);

            // Stale — delete it and proceed with acquisition.
            TryDeleteMeta(metaPath);
        }

        // ── Build descriptor ───────────────────────────────────────────────
        var descriptor = new DeploymentLockDescriptor
        {
            LockFileVersion = 2,
            ResourceKey     = resource.Key,
            ResourceType    = resource.Type,
            ResourcePath    = resource.Path,
            SessionId       = sessionId,
            ConfigurationId = config.Id,
            MachineName     = Environment.MachineName,
            OwnerUser       = Environment.UserName,
            OwnerProcessId  = Environment.ProcessId,
            AcquiredAt      = DateTimeOffset.UtcNow,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            DeploymentName  = config.Name
        };

        var json = JsonSerializer.Serialize(descriptor, DeploymentJsonOptions.Create());

        // ── Phase 1: Write provisional (crash-safe, fully flushed) ─────────
        try
        {
            WriteProvisional(provisionalPath, json);
        }
        catch (Exception ex)
        {
            // Can't even write the provisional — disk full, permissions, etc.
            TryDeleteFile(provisionalPath);
            throw new InvalidOperationException(
                $"Could not write lock provisional file '{provisionalPath}': {ex.Message}", ex);
        }

        // ── Phase 2: Atomic rename — the exclusive gate ────────────────────
        try
        {
            // File.Move with overwrite:false throws IOException if destination exists.
            // On Windows this is equivalent to an atomic rename — serialised at the kernel level.
            // Exactly one concurrent process can succeed; all others throw IOException.
            File.Move(provisionalPath, metaPath, overwrite: false);

            // SUCCESS: we now own the lock.
            return (descriptor, null);
        }
        catch (IOException)
        {
            // Another process won the rename race — the lock belongs to them.
            // Our provisional is still on disk; clean it up.
            TryDeleteFile(provisionalPath);

            // Read the winner's metadata for the conflict report.
            // Because we write provisional BEFORE renaming, the winner's .meta always
            // contains valid JSON at this point (the winner completed their own write).
            var winner = await TryReadMetaAsync(metaPath, cancellationToken).ConfigureAwait(false);

            // winner could be null if the owner released between our failed rename and now —
            // in that case we lost a very tight race; return a synthetic descriptor.
            return (null, winner ?? new DeploymentLockDescriptor
            {
                ResourceKey  = resource.Key,
                ResourceType = resource.Type,
                ResourcePath = resource.Path,
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Release
    // ══════════════════════════════════════════════════════════════════════════

    public Task ReleaseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        foreach (var meta in SafeEnumerateFiles(_lockRoot, "*.meta"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = File.ReadAllText(meta);
                var desc = TryDeserializeMeta(json);
                if (desc?.SessionId == sessionId)
                    TryDeleteMeta(meta);
            }
            catch { /* best effort */ }
        }

        // Also remove any orphaned provisionals for this session.
        CleanupSessionProvisionals(sessionId);

        // Remove legacy v1 .lock sentinels that belonged to this session (if any remain).
        CleanupLegacySentinelsForSession(sessionId);

        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Heartbeat
    // ══════════════════════════════════════════════════════════════════════════

    public async Task HeartbeatAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        foreach (var meta in SafeEnumerateFiles(_lockRoot, "*.meta"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false);
                var desc = TryDeserializeMeta(json);
                if (desc?.SessionId != sessionId) continue;

                // Update heartbeat timestamp atomically.
                desc.LastHeartbeatAt = DateTimeOffset.UtcNow;
                var updated = JsonSerializer.Serialize(desc, DeploymentJsonOptions.Create());
                await Persistence.AtomicFileWriter.WriteAllTextAsync(meta, updated, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch { /* best effort — heartbeat failures are non-fatal */ }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // List / Query
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<DeploymentLockDescriptor>> ListActiveLocksAsync(
        CancellationToken cancellationToken = default)
    {
        var list = new List<DeploymentLockDescriptor>();
        foreach (var meta in SafeEnumerateFiles(_lockRoot, "*.meta"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false);
                var desc = TryDeserializeMeta(json);
                if (desc is not null) list.Add(desc);
            }
            catch { /* skip */ }
        }
        return list;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Stale cleanup (lightweight, called before every acquire)
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<int> CleanupStaleLocksAsync(
        TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var removed = 0;

        // ── Clean stale .meta files (normal and cross-machine) ─────────────
        foreach (var meta in SafeEnumerateFiles(_lockRoot, "*.meta"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false);
                var desc = TryDeserializeMeta(json);

                if (desc is null)
                {
                    // Corrupt metadata → remove immediately.
                    TryDeleteMeta(meta);
                    removed++;
                    continue;
                }

                if (!desc.IsStale(maxAge)) continue;
                if (IsProcessAlive(desc.OwnerProcessId)) continue;

                TryDeleteMeta(meta);
                removed++;
            }
            catch { /* skip */ }
        }

        // ── Clean orphaned provisionals (.meta.tmp) ────────────────────────
        foreach (var tmp in SafeEnumerateFiles(_lockRoot, "*.meta.tmp"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(tmp);
                if (DateTimeOffset.UtcNow - info.LastWriteTimeUtc > OrphanedProvisionalMaxAge)
                {
                    TryDeleteFile(tmp);
                    removed++;
                }
            }
            catch { /* skip */ }
        }

        // ── Clean legacy v1 .lock sentinel files (no metadata, not needed in v2) ──
        foreach (var legacy in SafeEnumerateFiles(_lockRoot, "*.lock"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // A v1 .lock file is always accompanied by a .meta file.
                // If the .meta is gone (or stale), the .lock sentinel is orphaned.
                var metaPartner = legacy + ".meta";
                if (!File.Exists(metaPartner))
                {
                    TryDeleteFile(legacy);
                    removed++;
                    continue;
                }

                // If .meta is stale and process is gone, remove both.
                var json = await File.ReadAllTextAsync(metaPartner, cancellationToken).ConfigureAwait(false);
                var desc = TryDeserializeMeta(json);
                if (desc is not null && desc.IsStale(maxAge) && !IsProcessAlive(desc.OwnerProcessId))
                {
                    TryDeleteFile(legacy);
                    TryDeleteMeta(metaPartner);
                    removed++;
                }
            }
            catch { /* skip */ }
        }

        return removed;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Full Reconciliation (startup scan with structured diagnostics)
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<LockReconciliationReport> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var sw      = Stopwatch.StartNew();
        var records = new List<LockDiagnosticRecord>();
        var report  = new LockReconciliationReport { RanAt = DateTimeOffset.UtcNow };

        // ── 1. Legacy v1 .lock sentinels ───────────────────────────────────
        foreach (var legacyLock in SafeEnumerateFiles(_lockRoot, "*.lock"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var metaPartner = legacyLock + ".meta";
                if (!File.Exists(metaPartner))
                {
                    // Orphaned sentinel: no metadata at all.
                    TryDeleteFile(legacyLock);
                    records.Add(new LockDiagnosticRecord
                    {
                        FilePath    = legacyLock,
                        Action      = LockReconciliationAction.RemovedLegacySentinel,
                        Reason      = "Legacy v1 .lock sentinel has no .meta partner — orphaned by crash or protocol change.",
                    });
                    report.LegacySentinelFilesRemoved++;
                    continue;
                }

                // Has a .meta partner — check if the whole v1 lock is stale.
                var json = await File.ReadAllTextAsync(metaPartner, cancellationToken).ConfigureAwait(false);
                var desc = TryDeserializeMeta(json);

                if (desc is not null && !desc.IsStale(DefaultStaleLockAge) && IsProcessAlive(desc.OwnerProcessId))
                {
                    // v1 lock is still active — leave the .meta, only remove the .lock sentinel.
                    TryDeleteFile(legacyLock);
                    records.Add(new LockDiagnosticRecord
                    {
                        FilePath      = legacyLock,
                        Action        = LockReconciliationAction.RemovedLegacySentinel,
                        Reason        = "Removed legacy v1 .lock sentinel; .meta remains active.",
                        OwnerUser     = desc.OwnerUser,
                        OwnerPid      = desc.OwnerProcessId,
                        OwnerMachine  = desc.MachineName,
                        OwnerSessionId = desc.SessionId,
                        AcquiredAt    = desc.AcquiredAt,
                    });
                    report.LegacySentinelFilesRemoved++;
                }
                // (stale v1 locks are handled in the .meta pass below)
            }
            catch { /* skip */ }
        }

        // ── 2. Orphaned provisional files (.meta.tmp) ──────────────────────
        foreach (var provisional in SafeEnumerateFiles(_lockRoot, "*.meta.tmp"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(provisional);
                var age  = DateTimeOffset.UtcNow - info.LastWriteTimeUtc;
                if (age > OrphanedProvisionalMaxAge)
                {
                    TryDeleteFile(provisional);
                    records.Add(new LockDiagnosticRecord
                    {
                        FilePath     = provisional,
                        Action       = LockReconciliationAction.RemovedOrphanedProvisional,
                        Reason       = $"Provisional lock file is {age.TotalMinutes:F0} min old — " +
                                       "process crashed before atomic rename completed.",
                        HeartbeatAge = age,
                    });
                    report.OrphanedProvisionalsRemoved++;
                }
                // Young provisionals (< 10 min) might belong to an in-progress acquisition.
            }
            catch { /* skip */ }
        }

        // ── 3. Live .meta files ────────────────────────────────────────────
        foreach (var meta in SafeEnumerateFiles(_lockRoot, "*.meta"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? json = null;
            try
            {
                json = await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false);
            }
            catch { /* unreadable — treat as corrupt */ }

            var desc = json is not null ? TryDeserializeMeta(json) : null;

            if (desc is null)
            {
                // Corrupt or empty metadata.
                TryDeleteMeta(meta);
                records.Add(new LockDiagnosticRecord
                {
                    FilePath = meta,
                    Action   = LockReconciliationAction.RemovedCorrupt,
                    Reason   = "Lock metadata file is missing, empty, or contains invalid JSON.",
                });
                report.CorruptMetaFilesRemoved++;
                continue;
            }

            var heartbeatAge = DateTimeOffset.UtcNow - desc.LastHeartbeatAt;

            // Cross-machine abandonment: different machine, heartbeat expired.
            if (desc.IsFromDifferentMachine && heartbeatAge > CrossMachineAbandonMaxAge)
            {
                TryDeleteMeta(meta);
                records.Add(new LockDiagnosticRecord
                {
                    FilePath       = meta,
                    ResourceKey    = desc.ResourceKey,
                    Action         = LockReconciliationAction.RemovedCrossMachineAbandoned,
                    Reason         = $"Lock held by machine '{desc.MachineName}' (this machine: '{Environment.MachineName}') " +
                                     $"and heartbeat is {heartbeatAge.TotalHours:F1}h old.",
                    OwnerUser      = desc.OwnerUser,
                    OwnerPid       = desc.OwnerProcessId,
                    OwnerMachine   = desc.MachineName,
                    OwnerSessionId = desc.SessionId,
                    AcquiredAt     = desc.AcquiredAt,
                    HeartbeatAge   = heartbeatAge,
                });
                report.CrossMachineAbandonedRemoved++;
                continue;
            }

            // Stale on this machine: heartbeat expired AND owning process is gone.
            if (desc.IsStale(DefaultStaleLockAge) && !IsProcessAlive(desc.OwnerProcessId))
            {
                TryDeleteMeta(meta);
                records.Add(new LockDiagnosticRecord
                {
                    FilePath       = meta,
                    ResourceKey    = desc.ResourceKey,
                    Action         = LockReconciliationAction.RemovedStale,
                    Reason         = $"Heartbeat is {heartbeatAge.TotalHours:F1}h old and " +
                                     $"owning process (PID {desc.OwnerProcessId}) is no longer running.",
                    OwnerUser      = desc.OwnerUser,
                    OwnerPid       = desc.OwnerProcessId,
                    OwnerMachine   = desc.MachineName,
                    OwnerSessionId = desc.SessionId,
                    AcquiredAt     = desc.AcquiredAt,
                    HeartbeatAge   = heartbeatAge,
                });
                report.StaleLocksRemoved++;
                continue;
            }

            // Lock is valid and currently held.
            records.Add(new LockDiagnosticRecord
            {
                FilePath       = meta,
                ResourceKey    = desc.ResourceKey,
                Action         = LockReconciliationAction.Kept,
                Reason         = $"Lock is active (PID {desc.OwnerProcessId}, " +
                                 $"heartbeat {heartbeatAge.TotalMinutes:F0} min ago).",
                OwnerUser      = desc.OwnerUser,
                OwnerPid       = desc.OwnerProcessId,
                OwnerMachine   = desc.MachineName,
                OwnerSessionId = desc.SessionId,
                AcquiredAt     = desc.AcquiredAt,
                HeartbeatAge   = heartbeatAge,
            });
            report.ActiveLocksKept++;
        }

        sw.Stop();
        report.Duration = sw.Elapsed;
        return report with { Records = records.AsReadOnly() };
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Private helpers
    // ══════════════════════════════════════════════════════════════════════════

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// <summary>Final lock metadata file path for a resource key.</summary>
    private string MetaFilePath(string resourceKey)
        => Path.Combine(_lockRoot, $"{SanitizeKey(resourceKey)}.meta");

    /// <summary>
    /// Provisional file path used during acquisition.
    /// Unique per (resource, session) — prevents collision between concurrent callers.
    /// </summary>
    private string ProvisionalFilePath(string resourceKey, Guid sessionId)
        => Path.Combine(_lockRoot, $"{SanitizeKey(resourceKey)}-{sessionId:N}.meta.tmp");

    private static string SanitizeKey(string key)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            key = key.Replace(c, '_');
        return key.Length > 120
            ? Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..64]
            : key;
    }

    // ── Provisional write ─────────────────────────────────────────────────────

    /// <summary>
    /// Write <paramref name="json"/> to <paramref name="path"/> with explicit fsync.
    /// The file is fully durable on disk before this returns.
    /// This is the "phase 1" of the two-phase lock acquisition protocol.
    /// </summary>
    private static void WriteProvisional(string path, string json)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: false);
        using var writer = new StreamWriter(fs, Encoding.UTF8, leaveOpen: false);
        writer.Write(json);
        writer.Flush();
        fs.Flush(flushToDisk: true);   // fsync — ensure bytes reach storage
    }

    // ── Meta read ─────────────────────────────────────────────────────────────

    private static async Task<DeploymentLockDescriptor?> TryReadMetaAsync(
        string metaPath, CancellationToken ct)
    {
        if (!File.Exists(metaPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false);
            return TryDeserializeMeta(json);
        }
        catch { return null; }
    }

    private static DeploymentLockDescriptor? TryDeserializeMeta(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DeploymentLockDescriptor>(json, DeploymentJsonOptions.Create()); }
        catch { return null; }
    }

    // ── Deletion helpers ──────────────────────────────────────────────────────

    private static void TryDeleteMeta(string metaPath)
    {
        TryDeleteFile(metaPath);
        // Also remove a co-located v1 .lock sentinel if one exists (backward compat cleanup).
        // The v1 sentinel path is: metaPath without the ".meta" suffix → "{key}.lock"
        // But our current .meta paths are "{key}.meta" and v1 .lock paths are "{key}.lock"
        // (where {key} = sanitized resource key, not the raw key with dots).
        // Since .meta files don't have a .lock sibling in v2, this is a no-op for v2.
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    // ── Intra-process gating ──────────────────────────────────────────────────

    private SemaphoreSlim GetResourceGate(string resourceKey)
    {
        lock (_gatesLock)
        {
            if (!_resourceGates.TryGetValue(resourceKey, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _resourceGates[resourceKey] = sem;
            }
            return sem;
        }
    }

    // ── Cleanup helpers ───────────────────────────────────────────────────────

    private void CleanupSessionProvisionals(Guid sessionId)
    {
        var suffix = $"-{sessionId:N}.meta.tmp";
        foreach (var tmp in SafeEnumerateFiles(_lockRoot, $"*{suffix}"))
            TryDeleteFile(tmp);
    }

    private void CleanupLegacySentinelsForSession(Guid sessionId)
    {
        foreach (var legacy in SafeEnumerateFiles(_lockRoot, "*.lock"))
        {
            try
            {
                var metaPartner = legacy + ".meta";
                if (!File.Exists(metaPartner))
                {
                    TryDeleteFile(legacy);
                    continue;
                }
                var json = File.ReadAllText(metaPartner);
                var desc = TryDeserializeMeta(json);
                if (desc?.SessionId == sessionId)
                    TryDeleteFile(legacy);
            }
            catch { /* best effort */ }
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern)
    {
        try { return Directory.EnumerateFiles(directory, pattern).ToList(); }
        catch { return []; }
    }

    // ── Process liveness ─────────────────────────────────────────────────────

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try { return Process.GetProcessById(pid) is not null; }
        catch { return false; }
    }

    // ── Resource list ─────────────────────────────────────────────────────────

    private static List<(string Key, string Type, string Path)> BuildResourceList(
        DeploymentConfiguration config)
    {
        var list = new List<(string, string, string)>();
        if (!string.IsNullOrWhiteSpace(config.Paths.MiddlewareHome))
            list.Add(("mw:" + config.Paths.MiddlewareHome.ToLowerInvariant(),
                      "OracleHome", config.Paths.MiddlewareHome));
        if (!string.IsNullOrWhiteSpace(config.Paths.OracleInventory))
            list.Add(("inv:" + config.Paths.OracleInventory.ToLowerInvariant(),
                      "CentralInventory", config.Paths.OracleInventory));
        var domainHome = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);
        if (!string.IsNullOrWhiteSpace(domainHome))
            list.Add(("dom:" + domainHome.ToLowerInvariant(), "DomainHome", domainHome));
        return list;
    }

    // ── Conflict message ──────────────────────────────────────────────────────

    private static string BuildConflictMessage(IReadOnlyList<DeploymentLockDescriptor> conflicts)
    {
        var first = conflicts[0];
        return $"Deployment lock held on {first.ResourceType} '{first.ResourcePath}' " +
               $"by {first.OwnerUser}@{first.MachineName} " +
               $"(session {first.SessionId:N}, PID {first.OwnerProcessId}, " +
               $"since {first.AcquiredAt:u}).";
    }
}
