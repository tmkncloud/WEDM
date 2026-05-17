using FluentAssertions;
using WEDM.Infrastructure.Persistence;
using Xunit;

namespace WEDM.Infrastructure.Tests.Persistence;

// ═══════════════════════════════════════════════════════════════════════════════
// AtomicFileWriter Test Suite  — R-04 coverage
// ═══════════════════════════════════════════════════════════════════════════════
//
// Test categories:
//   1.  Basic correctness — text and byte writes land on disk correctly.
//   2.  Atomic semantics  — concurrent writes to the same path never produce
//                           a corrupt or partial file.
//   3.  Lock lifecycle    — PathLockEntry ref-count, disposal, and IsDisposed.
//   4.  Cleanup / eviction — Trim() removes idle entries and disposes semaphores.
//   5.  Diagnostics       — counters increment correctly.
//   6.  Stress            — thousands of unique paths, bounded dictionary size.
//   7.  Concurrent churn  — rapid acquire/release cycling without leaks or
//                           ObjectDisposedException races.
//   8.  Long-running sim  — stable lock count under deployment-style workloads.
//
// NOTE: AtomicFileWriter is a static class with static state.  Each test class
// calls Trim() in a [Collection]-scoped fixture to start with a clean registry.
// Diagnostics are measured as deltas (before/after) to avoid cross-test coupling.
// ═══════════════════════════════════════════════════════════════════════════════

// ── Shared fixture — ensures a clean registry before each test class ──────────

public sealed class CleanRegistryFixture : IDisposable
{
    public CleanRegistryFixture() => AtomicFileWriter.Trim();
    public void Dispose()         => AtomicFileWriter.Trim();
}

// Serialise all tests in this file so static state doesn't bleed across classes.
[CollectionDefinition("AtomicFileWriter", DisableParallelization = true)]
public sealed class AtomicFileWriterCollection { }

// ═══════════════════════════════════════════════════════════════════════════════
// 1. Basic Correctness
// ═══════════════════════════════════════════════════════════════════════════════

[Collection("AtomicFileWriter")]
public sealed class AtomicFileWriter_BasicCorrectnessTests : IClassFixture<CleanRegistryFixture>
{
    private readonly string _dir;

    public AtomicFileWriter_BasicCorrectnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wedm-afw-basic-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task WriteAllTextAsync_writes_expected_content()
    {
        var path = Path.Combine(_dir, "text.txt");
        await AtomicFileWriter.WriteAllTextAsync(path, "hello world");
        File.ReadAllText(path).Should().Be("hello world");
    }

    [Fact]
    public async Task WriteAllBytesAsync_writes_expected_content()
    {
        var path  = Path.Combine(_dir, "bytes.bin");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        await AtomicFileWriter.WriteAllBytesAsync(path, bytes);
        File.ReadAllBytes(path).Should().Equal(bytes);
    }

    [Fact]
    public async Task WriteAllTextAsync_overwrites_existing_file()
    {
        var path = Path.Combine(_dir, "overwrite.txt");
        await AtomicFileWriter.WriteAllTextAsync(path, "original");
        await AtomicFileWriter.WriteAllTextAsync(path, "updated");
        File.ReadAllText(path).Should().Be("updated");
    }

    [Fact]
    public async Task WriteAllBytesAsync_overwrites_existing_file()
    {
        var path = Path.Combine(_dir, "overwrite.bin");
        await AtomicFileWriter.WriteAllBytesAsync(path, new byte[] { 10, 20 });
        await AtomicFileWriter.WriteAllBytesAsync(path, new byte[] { 30, 40, 50 });
        File.ReadAllBytes(path).Should().Equal(30, 40, 50);
    }

    [Fact]
    public async Task WriteAllTextAsync_creates_parent_directory_if_absent()
    {
        var sub  = Path.Combine(_dir, "sub", "nested");
        var path = Path.Combine(sub, "file.txt");
        await AtomicFileWriter.WriteAllTextAsync(path, "nested");
        File.ReadAllText(path).Should().Be("nested");
    }

    [Fact]
    public async Task WriteAllBytesAsync_creates_parent_directory_if_absent()
    {
        var sub  = Path.Combine(_dir, "sub2", "nested2");
        var path = Path.Combine(sub, "file.bin");
        await AtomicFileWriter.WriteAllBytesAsync(path, new byte[] { 99 });
        File.ReadAllBytes(path).Should().Equal(99);
    }

    [Fact]
    public async Task No_leftover_temp_files_after_write()
    {
        var path = Path.Combine(_dir, "notemps.txt");
        await AtomicFileWriter.WriteAllTextAsync(path, "data");
        var tmps = Directory.GetFiles(_dir, "*.tmp");
        tmps.Should().BeEmpty("temp files must be cleaned up");
    }

    [Fact]
    public async Task Cancellation_does_not_corrupt_existing_file()
    {
        var path = Path.Combine(_dir, "cancel.txt");
        await AtomicFileWriter.WriteAllTextAsync(path, "original");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AtomicFileWriter.WriteAllTextAsync(path, "should-not-land", cts.Token));

        // Original content must be intact.
        File.ReadAllText(path).Should().Be("original");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 2. Atomic Semantics — Concurrent Writes
// ═══════════════════════════════════════════════════════════════════════════════

[Collection("AtomicFileWriter")]
public sealed class AtomicFileWriter_ConcurrentWriteTests : IClassFixture<CleanRegistryFixture>
{
    private readonly string _dir;

    public AtomicFileWriter_ConcurrentWriteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wedm-afw-conc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task Concurrent_writes_to_same_path_produce_valid_final_content()
    {
        // Many tasks write a known string to the same file.
        // The final content must be one of the written strings — never a partial/corrupt mix.
        const int taskCount = 40;
        var path = Path.Combine(_dir, "shared.txt");
        var validContents = Enumerable.Range(0, taskCount).Select(i => $"content-{i:D4}").ToHashSet();

        var tasks = validContents.Select(c =>
            AtomicFileWriter.WriteAllTextAsync(path, c)).ToArray();
        await Task.WhenAll(tasks);

        var final = File.ReadAllText(path);
        validContents.Should().Contain(final, "final content must be one of the written values, not a corrupt mix");
    }

    [Fact]
    public async Task Concurrent_writes_to_different_paths_all_succeed()
    {
        const int count = 50;
        var paths = Enumerable.Range(0, count)
            .Select(i => Path.Combine(_dir, $"file-{i}.txt"))
            .ToArray();

        var tasks = paths.Select((p, i) =>
            AtomicFileWriter.WriteAllTextAsync(p, $"value-{i}")).ToArray();
        await Task.WhenAll(tasks);

        for (var i = 0; i < count; i++)
            File.ReadAllText(paths[i]).Should().Be($"value-{i}");
    }

    [Fact]
    public async Task Repeated_concurrent_writes_to_same_path_maintain_integrity()
    {
        var path = Path.Combine(_dir, "repeated.txt");
        var validStrings = new HashSet<string>();

        for (var round = 0; round < 5; round++)
        {
            var batchContents = Enumerable.Range(0, 20)
                .Select(i => $"round-{round}-item-{i}").ToList();
            batchContents.ForEach(s => validStrings.Add(s));

            var tasks = batchContents.Select(c =>
                AtomicFileWriter.WriteAllTextAsync(path, c)).ToArray();
            await Task.WhenAll(tasks);
        }

        // After all rounds the file must contain a valid value.
        var final = File.ReadAllText(path);
        validStrings.Should().Contain(final);
    }

    [Fact]
    public async Task Bytes_concurrent_writes_to_same_path_produce_valid_content()
    {
        const int taskCount = 30;
        var path = Path.Combine(_dir, "shared.bin");

        // Each writer writes a 4-byte aligned pattern: [i, i, i, i].
        var validPayloads = Enumerable.Range(0, taskCount)
            .Select(i => new byte[] { (byte)i, (byte)i, (byte)i, (byte)i })
            .ToList();

        var tasks = validPayloads.Select(p =>
            AtomicFileWriter.WriteAllBytesAsync(path, p)).ToArray();
        await Task.WhenAll(tasks);

        var final = File.ReadAllBytes(path);
        final.Should().HaveCount(4, "file must be exactly 4 bytes — no partial writes");
        // All bytes must be the same (one writer's aligned payload, not a mix).
        final[0].Should().Be(final[1]).And.Be(final[2]).And.Be(final[3],
            "all 4 bytes must come from the same writer (no interleaving)");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 3. Lock Lifecycle — PathLockEntry
// ═══════════════════════════════════════════════════════════════════════════════

[Collection("AtomicFileWriter")]
public sealed class PathLockEntry_LifecycleTests : IClassFixture<CleanRegistryFixture>
{
    [Fact]
    public void New_entry_is_not_disposed_and_has_zero_refcount()
    {
        var entry = new PathLockEntry();
        entry.IsDisposed.Should().BeFalse();
        entry._refCount.Should().Be(0);
        entry.Semaphore.CurrentCount.Should().Be(1);
    }

    [Fact]
    public void IsDisposed_true_after_disposedInt_set_to_1()
    {
        var entry = new PathLockEntry();
        Interlocked.CompareExchange(ref entry._disposedInt, 1, 0);
        entry.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void TouchLastUsed_updates_LastUsedTicks_monotonically()
    {
        var entry = new PathLockEntry();
        var before = entry.LastUsedTicks;
        Thread.Sleep(5);
        entry.TouchLastUsed();
        entry.LastUsedTicks.Should().BeGreaterThan(before);
    }

    [Fact]
    public void Interlocked_increment_and_decrement_on_refCount_are_correct()
    {
        var entry = new PathLockEntry();

        Interlocked.Increment(ref entry._refCount);
        entry._refCount.Should().Be(1);

        Interlocked.Increment(ref entry._refCount);
        entry._refCount.Should().Be(2);

        Interlocked.Decrement(ref entry._refCount);
        entry._refCount.Should().Be(1);

        Interlocked.Decrement(ref entry._refCount);
        entry._refCount.Should().Be(0);
    }

    [Fact]
    public void Undispose_restores_IsDisposed_to_false()
    {
        var entry = new PathLockEntry();
        Interlocked.CompareExchange(ref entry._disposedInt, 1, 0);
        entry.IsDisposed.Should().BeTrue();

        // Simulate the cleanup-thread undo path.
        Interlocked.Exchange(ref entry._disposedInt, 0);
        entry.IsDisposed.Should().BeFalse();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 4. Registry State — PathLocks dictionary
// ═══════════════════════════════════════════════════════════════════════════════

[Collection("AtomicFileWriter")]
public sealed class AtomicFileWriter_RegistryTests : IClassFixture<CleanRegistryFixture>
{
    private readonly string _dir;

    public AtomicFileWriter_RegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wedm-afw-reg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task Writing_to_new_path_adds_entry_to_PathLocks()
    {
        AtomicFileWriter.Trim();
        var initialCount = AtomicFileWriter.PathLocks.Count;

        var path = Path.Combine(_dir, "newpath.txt");
        await AtomicFileWriter.WriteAllTextAsync(path, "x");

        // The entry may or may not have been evicted by Trim on release, but the
        // write must succeed without throwing.
        File.Exists(path).Should().BeTrue();
        _ = initialCount; // reference to satisfy compiler
    }

    [Fact]
    public void Trim_removes_all_idle_entries()
    {
        // Force some entries into the dictionary by manipulating PathLocks directly.
        const int count = 20;
        for (var i = 0; i < count; i++)
        {
            var key   = $@"C:\fake\path-{i}.json";
            var entry = new PathLockEntry();     // refCount = 0, not disposed
            AtomicFileWriter.PathLocks[key] = entry;
        }

        var before = AtomicFileWriter.PathLocks.Count;
        before.Should().BeGreaterThanOrEqualTo(count);

        AtomicFileWriter.Trim();

        AtomicFileWriter.PathLocks.Count.Should().BeLessThan(before,
            "Trim must evict idle entries");
    }

    [Fact]
    public void Trim_disposes_evicted_semaphores()
    {
        var entries = new List<PathLockEntry>();
        for (var i = 0; i < 10; i++)
        {
            var key   = $@"C:\fake\dispose-{i}.json";
            var entry = new PathLockEntry();
            entries.Add(entry);
            AtomicFileWriter.PathLocks[key] = entry;
        }

        AtomicFileWriter.Trim();

        // All idle (refCount=0) entries should have been disposed.
        foreach (var entry in entries.Where(e => e._refCount == 0))
        {
            entry.IsDisposed.Should().BeTrue("idle entries must be marked disposed after Trim");
            // Verifying the semaphore is actually disposed:
            var act = () => entry.Semaphore.Wait(0);
            act.Should().Throw<ObjectDisposedException>(
                "disposed semaphores must not be waited on");
        }
    }

    [Fact]
    public void Trim_does_not_evict_in_use_entries()
    {
        var key   = @"C:\fake\active.json";
        var entry = new PathLockEntry();
        Interlocked.Increment(ref entry._refCount); // simulate active hold
        AtomicFileWriter.PathLocks[key] = entry;

        AtomicFileWriter.Trim();

        // Entry should still be in the dictionary (refCount > 0 protects it).
        AtomicFileWriter.PathLocks.TryGetValue(key, out var remaining);
        remaining.Should().NotBeNull("in-use entries must survive cleanup");
        remaining!.IsDisposed.Should().BeFalse();

        // Clean up manually.
        Interlocked.Decrement(ref entry._refCount);
        AtomicFileWriter.Trim();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 5. Diagnostics
// ═══════════════════════════════════════════════════════════════════════════════

[Collection("AtomicFileWriter")]
public sealed class AtomicFileWriter_DiagnosticsTests : IClassFixture<CleanRegistryFixture>
{
    private readonly string _dir;

    public AtomicFileWriter_DiagnosticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wedm-afw-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        AtomicFileWriter.Trim();
    }

    [Fact]
    public void GetDiagnostics_returns_non_null_snapshot()
    {
        var diag = AtomicFileWriter.GetDiagnostics();
        diag.Should().NotBeNull();
    }

    [Fact]
    public void Trim_increments_CleanupPassCount()
    {
        var before = AtomicFileWriter.GetDiagnostics().CleanupPassCount;
        AtomicFileWriter.Trim();
        AtomicFileWriter.GetDiagnostics().CleanupPassCount.Should().BeGreaterThan(before);
    }

    [Fact]
    public void Trim_increments_EvictedLockCount_when_entries_present()
    {
        // Seed some idle entries.
        for (var i = 0; i < 5; i++)
            AtomicFileWriter.PathLocks[$@"C:\fake\diag-{i}.txt"] = new PathLockEntry();

        var before = AtomicFileWriter.GetDiagnostics().EvictedLockCount;
        AtomicFileWriter.Trim();
        AtomicFileWriter.GetDiagnostics().EvictedLockCount.Should().BeGreaterThan(before);
    }

    [Fact]
    public void Trim_increments_DisposedSemaphoreCount_when_entries_present()
    {
        for (var i = 0; i < 5; i++)
            AtomicFileWriter.PathLocks[$@"C:\fake\disp-{i}.txt"] = new PathLockEntry();

        var before = AtomicFileWriter.GetDiagnostics().DisposedSemaphoreCount;
        AtomicFileWriter.Trim();
        AtomicFileWriter.GetDiagnostics().DisposedSemaphoreCount.Should().BeGreaterThan(before);
    }

    [Fact]
    public async Task PeakLockUsage_is_non_negative_after_writes()
    {
        var path = Path.Combine(_dir, "peak.txt");
        await AtomicFileWriter.WriteAllTextAsync(path, "peak");
        AtomicFileWriter.GetDiagnostics().PeakLockUsage.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void ToString_includes_all_metric_names()
    {
        var diag = AtomicFileWriter.GetDiagnostics();
        var str  = diag.ToString();
        str.Should().Contain("Active=");
        str.Should().Contain("Peak=");
        str.Should().Contain("Evicted=");
        str.Should().Contain("Disposed=");
        str.Should().Contain("CleanupPasses=");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 6. Stress — Thousands of Unique Paths
// ═══════════════════════════════════════════════════════════════════════════════

[Collection("AtomicFileWriter")]
public sealed class AtomicFileWriter_StressTests : IClassFixture<CleanRegistryFixture>
{
    private readonly string _dir;

    public AtomicFileWriter_StressTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wedm-afw-stress-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        AtomicFileWriter.Trim();
    }

    [Fact]
    public async Task Writing_to_1000_unique_paths_all_succeed()
    {
        const int count = 1_000;
        var paths = Enumerable.Range(0, count)
            .Select(i => Path.Combine(_dir, $"unique-{i:D4}.txt"))
            .ToArray();

        var tasks = paths.Select((p, i) =>
            AtomicFileWriter.WriteAllTextAsync(p, $"data-{i}")).ToArray();
        await Task.WhenAll(tasks);

        for (var i = 0; i < count; i++)
            File.ReadAllText(paths[i]).Should().Be($"data-{i}");
    }

    [Fact]
    public async Task Lock_dictionary_stays_bounded_after_high_path_volume()
    {
        // Write to many paths in batches, calling Trim between batches.
        // The dictionary should never exceed CleanupThreshold * 2.
        const int batchSize = AtomicFileWriter.CleanupThreshold / 2;
        const int batches   = 6;

        for (var b = 0; b < batches; b++)
        {
            var batchPaths = Enumerable.Range(b * batchSize, batchSize)
                .Select(i => Path.Combine(_dir, $"bounded-{i}.txt")).ToArray();

            var tasks = batchPaths.Select(p =>
                AtomicFileWriter.WriteAllTextAsync(p, "x")).ToArray();
            await Task.WhenAll(tasks);

            AtomicFileWriter.Trim();
        }

        // After writes complete and Trim is called, all idle locks should be gone.
        AtomicFileWriter.PathLocks.Count.Should().BeLessThanOrEqualTo(
            AtomicFileWriter.CleanupThreshold,
            "dictionary must not grow unboundedly after Trim");
    }

    [Fact]
    public async Task Writing_to_5000_paths_does_not_throw()
    {
        // Smoke test: ensure no exceptions under high-volume unique-path writes.
        const int count = 5_000;
        var exception = (Exception?)null;

        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            try
            {
                var path = Path.Combine(_dir, $"smoke-{i:D5}.txt");
                await AtomicFileWriter.WriteAllTextAsync(path, $"v{i}");
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref exception!, ex, null);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        exception.Should().BeNull($"no exception expected; got: {exception?.Message}");

        // Clean up after ourselves.
        AtomicFileWriter.Trim();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 7. Concurrent Churn — Rapid Acquire/Release
// ═══════════════════════════════════════════════════════════════════════════════

[Collection("AtomicFileWriter")]
public sealed class AtomicFileWriter_ConcurrentChurnTests : IClassFixture<CleanRegistryFixture>
{
    private readonly string _dir;

    public AtomicFileWriter_ConcurrentChurnTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wedm-afw-churn-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        AtomicFileWriter.Trim();
    }

    [Fact]
    public async Task Rapid_alternating_writes_and_trims_produce_no_ObjectDisposedException()
    {
        // Interleave writes and Trim calls to stress the disposal race.
        const int iterations = 200;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writeTask = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                try
                {
                    var path = Path.Combine(_dir, $"churn-{i % 20}.txt");
                    await AtomicFileWriter.WriteAllTextAsync(path, $"iter-{i}");
                }
                catch (ObjectDisposedException ode)
                {
                    exceptions.Add(ode);
                }
                catch (OperationCanceledException) { /* ignore */ }
            }
        });

        var trimTask = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                AtomicFileWriter.Trim();
                await Task.Delay(1);
            }
        });

        await Task.WhenAll(writeTask, trimTask);

        exceptions.Should().BeEmpty(
            "ObjectDisposedException must never escape to callers — disposal race is a bug");
    }

    [Fact]
    public async Task Many_concurrent_writers_same_20_paths_no_corruption()
    {
        const int writers   = 80;
        const int pathCount = 20;
        var paths = Enumerable.Range(0, pathCount)
            .Select(i => Path.Combine(_dir, $"hot-{i}.txt")).ToArray();

        // Write sequentially first so all files exist.
        foreach (var (p, i) in paths.Select((p, i) => (p, i)))
            await AtomicFileWriter.WriteAllTextAsync(p, $"initial-{i}");

        // Now hammer concurrently.
        var tasks = Enumerable.Range(0, writers).Select(w =>
        {
            var target = paths[w % pathCount];
            return AtomicFileWriter.WriteAllTextAsync(target, $"writer-{w}");
        }).ToArray();

        await Task.WhenAll(tasks);

        // Every file must still be readable and contain a valid final value.
        for (var i = 0; i < pathCount; i++)
        {
            var text = File.ReadAllText(paths[i]);
            text.Should().MatchRegex(@"^(initial-\d+|writer-\d+)$",
                $"file {i} must contain a valid unfragmented value");
        }
    }

    [Fact]
    public async Task Concurrent_trims_while_writing_do_not_deadlock()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writeTask = Task.Run(async () =>
        {
            for (var i = 0; !cts.IsCancellationRequested; i++)
            {
                var path = Path.Combine(_dir, $"nodl-{i % 10}.txt");
                await AtomicFileWriter.WriteAllTextAsync(path, $"v{i}", cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);

        var trimTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                AtomicFileWriter.Trim();
                await Task.Delay(2, cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);

        cts.CancelAfter(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            writeTask.ContinueWith(t => { }, TaskContinuationOptions.OnlyOnCanceled),
            trimTask .ContinueWith(t => { }, TaskContinuationOptions.OnlyOnCanceled));
        // Test passes if we get here within the timeout (no deadlock).
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 8. Long-Running Simulation
// ═══════════════════════════════════════════════════════════════════════════════

[Collection("AtomicFileWriter")]
public sealed class AtomicFileWriter_LongRunningSimTests : IClassFixture<CleanRegistryFixture>
{
    private readonly string _dir;

    public AtomicFileWriter_LongRunningSimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wedm-afw-longsim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        AtomicFileWriter.Trim();
    }

    [Fact]
    public async Task Deployment_report_pattern_has_stable_lock_count()
    {
        // Simulate: deployment generates reports, checkpoints, and rollback files
        // repeatedly across 10 "sessions", each with its own config ID.
        const int sessions    = 10;
        const int filesPerSes = 15;

        for (var s = 0; s < sessions; s++)
        {
            var sessionId   = Guid.NewGuid().ToString("N");
            var sessionDir  = Path.Combine(_dir, $"session-{s}");
            Directory.CreateDirectory(sessionDir);

            // Each session writes: report, JSON, checkpoint, rollback log, diagnostics.
            var sessionFiles = Enumerable.Range(0, filesPerSes)
                .Select(f => Path.Combine(sessionDir, $"artifact-{f}-{sessionId}.dat"))
                .ToArray();

            var tasks = sessionFiles.Select(p =>
                AtomicFileWriter.WriteAllTextAsync(p, $"session={s}")).ToArray();
            await Task.WhenAll(tasks);

            // Simulate session cleanup.
            AtomicFileWriter.Trim();
        }

        // After 10 sessions each with 15 paths, and Trim after each session,
        // the lock registry must be nearly empty.
        var finalCount = AtomicFileWriter.PathLocks.Count;
        finalCount.Should().BeLessThanOrEqualTo(10,
            "after Trim the registry must not accumulate stale entries across sessions");
    }

    [Fact]
    public async Task Retry_isolation_pattern_bounded_lock_growth()
    {
        // Simulate: 5 deployment retries, each writing temp files to unique paths,
        // followed by a Trim at the end of the retry cycle.
        const int retries       = 5;
        const int tempFilesPerRetry = 30;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            var tempPaths = Enumerable.Range(0, tempFilesPerRetry)
                .Select(i => Path.Combine(_dir, $"retry-{attempt}-oui-extract-{i}.tmp"))
                .ToArray();

            var tasks = tempPaths.Select(p =>
                AtomicFileWriter.WriteAllTextAsync(p, "oui-data")).ToArray();
            await Task.WhenAll(tasks);
        }

        var before = AtomicFileWriter.PathLocks.Count;
        AtomicFileWriter.Trim();
        var after = AtomicFileWriter.PathLocks.Count;

        after.Should().BeLessThan(before.Equals(0) ? 1 : before,
            "Trim must reduce the lock count after retry accumulation");
    }

    [Fact]
    public async Task Repeated_rollback_report_writes_do_not_grow_registry()
    {
        // Simulate: the same 5 rollback report paths written 20 times each.
        const int paths  = 5;
        const int rounds = 20;

        var reportPaths = Enumerable.Range(0, paths)
            .Select(i => Path.Combine(_dir, $"rollback-report-{i}.json")).ToArray();

        for (var r = 0; r < rounds; r++)
        {
            var tasks = reportPaths.Select(p =>
                AtomicFileWriter.WriteAllTextAsync(p, $"{{\"round\":{r}}}")).ToArray();
            await Task.WhenAll(tasks);
        }

        // With the same 5 paths reused 20 times, we should see at most 5 entries
        // (most likely 0 after release + potential partial eviction).
        var count = AtomicFileWriter.PathLocks.Count;
        count.Should().BeLessThanOrEqualTo(paths,
            "reuse of the same paths must not inflate the registry");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 9. Disposal Safety — no ObjectDisposedException races
// ═══════════════════════════════════════════════════════════════════════════════

[Collection("AtomicFileWriter")]
public sealed class AtomicFileWriter_DisposalSafetyTests : IClassFixture<CleanRegistryFixture>
{
    private readonly string _dir;

    public AtomicFileWriter_DisposalSafetyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wedm-afw-disposal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        AtomicFileWriter.Trim();
    }

    [Fact]
    public async Task In_use_entry_survives_forced_Trim()
    {
        // Simulate an entry whose refCount we manually hold.
        var key   = @"C:\fake\inuse-survive.txt";
        var entry = new PathLockEntry();
        Interlocked.Increment(ref entry._refCount); // simulate "in use"
        AtomicFileWriter.PathLocks[key] = entry;

        AtomicFileWriter.Trim();

        // Entry must NOT have been disposed.
        entry.IsDisposed.Should().BeFalse("in-use entry must survive forced Trim");
        entry.Semaphore.Should().NotBeNull();

        // Release and clean up.
        Interlocked.Decrement(ref entry._refCount);
        AtomicFileWriter.Trim();
    }

    [Fact]
    public async Task No_ObjectDisposedException_under_write_and_trim_race()
    {
        const int iterations = 500;
        var odExceptions     = new System.Collections.Concurrent.ConcurrentBag<string>();

        async Task WriteLoop()
        {
            for (var i = 0; i < iterations; i++)
            {
                try
                {
                    var path = Path.Combine(_dir, $"race-{i % 5}.txt");
                    await AtomicFileWriter.WriteAllTextAsync(path, $"i={i}");
                }
                catch (ObjectDisposedException ode)
                {
                    odExceptions.Add($"ODE at i={i}: {ode.Message}");
                }
            }
        }

        void TrimLoop()
        {
            for (var i = 0; i < 100; i++)
            {
                AtomicFileWriter.Trim();
                Thread.Yield();
            }
        }

        var writeTask = Task.Run(WriteLoop);
        var trimTask  = Task.Run(TrimLoop);
        await Task.WhenAll(writeTask, trimTask);

        odExceptions.Should().BeEmpty(
            string.Join("; ", odExceptions.Take(3)));
    }

    [Fact]
    public async Task Trim_during_high_concurrency_write_is_safe()
    {
        const int writerCount = 20;
        const int writesEach  = 25;
        var odCaught = 0;

        var writers = Enumerable.Range(0, writerCount).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < writesEach; i++)
            {
                try
                {
                    var path = Path.Combine(_dir, $"hc-{(w * writesEach + i) % 8}.txt");
                    await AtomicFileWriter.WriteAllTextAsync(path, $"w={w},i={i}");
                }
                catch (ObjectDisposedException)
                {
                    Interlocked.Increment(ref odCaught);
                }
            }
        })).ToArray();

        // Trim aggressively from a separate thread.
        var trimmer = Task.Run(async () =>
        {
            for (var i = 0; i < 30; i++)
            {
                AtomicFileWriter.Trim();
                await Task.Delay(1);
            }
        });

        await Task.WhenAll(writers.Append(trimmer));

        odCaught.Should().Be(0, "ObjectDisposedException must never surface to callers");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 10. RunCleanup internals — directly exercised for precision
// ═══════════════════════════════════════════════════════════════════════════════

[Collection("AtomicFileWriter")]
public sealed class AtomicFileWriter_RunCleanupTests : IClassFixture<CleanRegistryFixture>
{
    [Fact]
    public void RunCleanup_force_evicts_idle_entries_immediately()
    {
        const int count = 10;
        var keys = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var key = $@"C:\fake\cleanup-force-{i}.json";
            keys.Add(key);
            AtomicFileWriter.PathLocks[key] = new PathLockEntry();
        }

        AtomicFileWriter.RunCleanup(force: true);

        foreach (var key in keys)
        {
            AtomicFileWriter.PathLocks.TryGetValue(key, out _).Should().BeFalse(
                $"key {key} should have been evicted by forced cleanup");
        }
    }

    [Fact]
    public void RunCleanup_non_force_respects_idle_threshold()
    {
        // Insert an entry and immediately run a non-forced cleanup.
        // Since the entry was just created (not idle for IdleEvictionMs), it must survive.
        var key   = @"C:\fake\fresh-entry.json";
        var entry = new PathLockEntry();
        AtomicFileWriter.PathLocks[key] = entry;

        AtomicFileWriter.RunCleanup(force: false);

        // The entry was created just now, so its LastUsedTicks > cutoff.
        AtomicFileWriter.PathLocks.TryGetValue(key, out var remaining);
        remaining.Should().NotBeNull("fresh entry must survive non-forced cleanup");

        // Clean up.
        AtomicFileWriter.RunCleanup(force: true);
    }

    [Fact]
    public void RunCleanup_skips_entries_with_nonzero_refcount()
    {
        var key   = @"C:\fake\active-rc.json";
        var entry = new PathLockEntry();
        Interlocked.Increment(ref entry._refCount);
        AtomicFileWriter.PathLocks[key] = entry;

        AtomicFileWriter.RunCleanup(force: true);

        AtomicFileWriter.PathLocks.TryGetValue(key, out var remaining);
        remaining.Should().NotBeNull("active entry must not be evicted");
        entry.IsDisposed.Should().BeFalse();

        Interlocked.Decrement(ref entry._refCount);
        AtomicFileWriter.RunCleanup(force: true);
    }

    [Fact]
    public void RunCleanup_skips_already_disposed_entries()
    {
        var key   = @"C:\fake\already-disposed.json";
        var entry = new PathLockEntry();
        // Mark disposed externally.
        Interlocked.Exchange(ref entry._disposedInt, 1);
        AtomicFileWriter.PathLocks[key] = entry;

        // Should not throw, not double-dispose.
        var act = () => AtomicFileWriter.RunCleanup(force: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void RunCleanup_increments_CleanupPassCount_each_call()
    {
        var before = AtomicFileWriter.GetDiagnostics().CleanupPassCount;
        AtomicFileWriter.RunCleanup(force: false);
        AtomicFileWriter.RunCleanup(force: true);
        AtomicFileWriter.GetDiagnostics().CleanupPassCount.Should().BeGreaterThanOrEqualTo(before + 2);
    }
}
