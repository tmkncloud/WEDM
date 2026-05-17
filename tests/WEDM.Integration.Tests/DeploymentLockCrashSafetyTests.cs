using System.Text.Json;
using FluentAssertions;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Deployment;
using Xunit;

namespace Orchestration.Integration.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// DeploymentLockCrashSafetyTests
// ═══════════════════════════════════════════════════════════════════════════════
//
// Covers R-02: Lock acquisition must be crash-safe, race-free, and self-healing.
//
// Test scenarios:
//   1.  Sequential acquire / release / re-acquire works correctly
//   2.  Orphaned .meta.tmp provisional is cleaned up by CleanupStaleLocksAsync
//   3.  Orphaned .meta.tmp provisional does NOT block a fresh acquisition
//   4.  Orphaned legacy v1 .lock sentinel (no .meta) is cleaned up
//   5.  Corrupt .meta file is cleaned up and does not block acquisition
//   6.  Stale .meta with dead PID is cleaned up
//   7.  Active lock from live PID is kept (not cleaned up)
//   8.  Concurrent acquisition — only one caller wins per resource
//   9.  All-or-nothing: partial acquisition across multiple resources rolls back on conflict
//  10.  Heartbeat updates LastHeartbeatAt atomically
//  11.  ReconcileAsync handles all orphan types and reports diagnostics
//  12.  Cross-machine abandoned lock is cleaned up by ReconcileAsync
//  13.  Acquisition succeeds after releasing a previously held lock
//  14.  Same session can re-acquire after prior release (idempotent path)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DeploymentLockCrashSafetyTests : IDisposable
{
    private readonly string             _lockRoot;
    private readonly DeploymentLockService _locks;

    public DeploymentLockCrashSafetyTests()
    {
        _lockRoot = Path.Combine(
            Path.GetTempPath(), "wedm-lock-crash-tests", Guid.NewGuid().ToString("N"));
        _locks = new DeploymentLockService(_lockRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_lockRoot)) Directory.Delete(_lockRoot, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DeploymentConfiguration MakeConfig(string home = @"C:\Oracle\MW") =>
        new()
        {
            Name  = "lock-test",
            Paths = new PathConfiguration
            {
                MiddlewareHome  = home,
                OracleInventory = home.Replace("MW", "Inventory"),
                DomainBase      = home.Replace("MW", "Domains"),
            },
            Domain = new DomainConfiguration { DomainName = "TestDomain" },
        };

    private static DeploymentLockDescriptor MakeDescriptor(
        string resourceKey,
        Guid? sessionId      = null,
        int?  pid            = null,
        DateTimeOffset? heartbeat = null,
        string? machine      = null) =>
        new()
        {
            LockFileVersion = 2,
            ResourceKey     = resourceKey,
            ResourceType    = "OracleHome",
            ResourcePath    = @"C:\Oracle\MW",
            SessionId       = sessionId ?? Guid.NewGuid(),
            MachineName     = machine ?? Environment.MachineName,
            OwnerUser       = "TestUser",
            OwnerProcessId  = pid ?? Environment.ProcessId,
            AcquiredAt      = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = heartbeat ?? DateTimeOffset.UtcNow,
            DeploymentName  = "lock-test",
        };

    /// <summary>Write a .meta file directly (simulates a lock held by external process).</summary>
    private void WriteMetaDirect(string resourceKey, DeploymentLockDescriptor desc)
    {
        // Match the same sanitised-key + ".meta" naming the service uses internally.
        // We do this by asking the file system what name the service would produce.
        // Simplest: use the same sanitization logic inline.
        var sanitized = SanitizeKeyForTest(resourceKey);
        var path = Path.Combine(_lockRoot, $"{sanitized}.meta");
        File.WriteAllText(path, JsonSerializer.Serialize(desc, DeploymentJsonOptions.Create()));
    }

    /// <summary>Write a provisional .meta.tmp file directly (simulates a crashed mid-acquisition).</summary>
    private void WriteProvisionalDirect(string resourceKey, Guid sessionId, string content = "{}")
    {
        var sanitized = SanitizeKeyForTest(resourceKey);
        var path = Path.Combine(_lockRoot, $"{sanitized}-{sessionId:N}.meta.tmp");
        File.WriteAllText(path, content);
    }

    /// <summary>Write an empty legacy v1 .lock sentinel (simulates pre-R02 code).</summary>
    private void WriteLegacySentinel(string resourceKey)
    {
        var sanitized = SanitizeKeyForTest(resourceKey);
        var path = Path.Combine(_lockRoot, $"{sanitized}.lock");
        File.WriteAllText(path, string.Empty);
    }

    private static string SanitizeKeyForTest(string key)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            key = key.Replace(c, '_');
        return key.Length > 120
            ? Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(key)))[..64]
            : key;
    }

    private string MetaPath(string resourceKey)
        => Path.Combine(_lockRoot, $"{SanitizeKeyForTest(resourceKey)}.meta");

    // ─────────────────────────────────────────────────────────────────────────
    // 1. Sequential acquire / release / re-acquire
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sequential_acquire_release_reacquire_succeeds()
    {
        var config = MakeConfig();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        var first = await _locks.TryAcquireAsync(config, s1);
        first.Acquired.Should().BeTrue("first caller acquires freely");
        first.AcquiredLocks.Should().NotBeEmpty();

        var second = await _locks.TryAcquireAsync(config, s2);
        second.Acquired.Should().BeFalse("second caller blocked while first holds the lock");
        second.ConflictingLocks.Should().NotBeEmpty();

        await _locks.ReleaseAsync(s1);

        var third = await _locks.TryAcquireAsync(config, s2);
        third.Acquired.Should().BeTrue("after release, second caller can acquire");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. Orphaned provisional IS cleaned up by CleanupStaleLocksAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupStaleLocksAsync_removes_old_orphaned_provisional()
    {
        var resourceKey = "mw:c:\\oracle\\mw";
        var sessionId   = Guid.NewGuid();

        // Simulate a provisional left by a crashed acquisition (> 10 min old).
        var sanitized = SanitizeKeyForTest(resourceKey);
        var tmpPath   = Path.Combine(_lockRoot, $"{sanitized}-{sessionId:N}.meta.tmp");
        File.WriteAllText(tmpPath, "{}");
        // Back-date the file to make it appear old.
        File.SetLastWriteTimeUtc(tmpPath, DateTime.UtcNow.AddMinutes(-15));

        var removed = await _locks.CleanupStaleLocksAsync(DeploymentLockService.DefaultStaleLockAge);
        removed.Should().BeGreaterThanOrEqualTo(1, "old provisional must be cleaned up");
        File.Exists(tmpPath).Should().BeFalse("provisional must be removed");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. Orphaned provisional does NOT block fresh acquisition
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Acquisition_succeeds_when_stale_provisional_exists()
    {
        // Simulate a YOUNG provisional (left just now, < 10 min) for a DIFFERENT session.
        // A young provisional belongs to an in-progress acquisition from another process.
        // But since the .meta file doesn't exist, the atomic rename will still succeed
        // for a new caller (the provisional is session-unique, not blocking).
        var config    = MakeConfig(@"C:\Oracle\MW_ProvisTest");
        var sessionId = Guid.NewGuid();
        WriteProvisionalDirect("mw:c:\\oracle\\mw_provistest", sessionId, "{}");

        // A new session should still be able to acquire (the provisional is not the lock).
        var freshSession = Guid.NewGuid();
        var result = await _locks.TryAcquireAsync(config, freshSession);
        result.Acquired.Should().BeTrue(
            "a session-specific provisional left by another process does not block acquisition");

        await _locks.ReleaseAsync(freshSession);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. Orphaned legacy v1 .lock sentinel is cleaned up
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Legacy_lock_sentinel_without_meta_is_cleaned_up()
    {
        var resourceKey = "mw:c:\\oracle\\mw_legacy";
        WriteLegacySentinel(resourceKey);

        var sentinelPath = Path.Combine(_lockRoot, $"{SanitizeKeyForTest(resourceKey)}.lock");
        File.Exists(sentinelPath).Should().BeTrue("sentinel exists before cleanup");

        var removed = await _locks.CleanupStaleLocksAsync(DeploymentLockService.DefaultStaleLockAge);
        removed.Should().BeGreaterThanOrEqualTo(1);
        File.Exists(sentinelPath).Should().BeFalse("orphaned legacy sentinel must be removed");
    }

    [Fact]
    public async Task ReconcileAsync_removes_legacy_lock_sentinel_without_meta()
    {
        var resourceKey = "mw:c:\\oracle\\mw_reclegacy";
        WriteLegacySentinel(resourceKey);

        var report = await _locks.ReconcileAsync();

        report.LegacySentinelFilesRemoved.Should().BeGreaterThanOrEqualTo(1);
        var record = report.Records.Should().Contain(r =>
            r.Action == LockReconciliationAction.RemovedLegacySentinel).Subject;
        record.Reason.Should().Contain("orphaned");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5. Corrupt .meta file is cleaned up and does not block acquisition
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Corrupt_meta_file_is_cleaned_and_acquisition_succeeds()
    {
        var config = MakeConfig(@"C:\Oracle\MW_CorruptTest");
        var resourceKey = "mw:c:\\oracle\\mw_corrupttest";

        // Plant a corrupt .meta file (invalid JSON simulating partial write from crash).
        var metaPath = MetaPath(resourceKey);
        File.WriteAllText(metaPath, "{ \"sessionId\": \"not-valid-json-truncated");

        File.Exists(metaPath).Should().BeTrue("corrupt meta exists before acquire");

        var sessionId = Guid.NewGuid();
        var result = await _locks.TryAcquireAsync(config, sessionId);

        result.Acquired.Should().BeTrue(
            "corrupt .meta must be treated as absent — acquisition must succeed");
        File.Exists(metaPath).Should().BeTrue("new valid .meta must exist after successful acquire");

        // Verify the new .meta is valid JSON with the correct session.
        var json = File.ReadAllText(metaPath);
        var desc = JsonSerializer.Deserialize<DeploymentLockDescriptor>(json, DeploymentJsonOptions.Create());
        desc.Should().NotBeNull();
        desc!.SessionId.Should().Be(sessionId);

        await _locks.ReleaseAsync(sessionId);
    }

    [Fact]
    public async Task ReconcileAsync_reports_corrupt_meta_as_RemovedCorrupt()
    {
        var resourceKey = "mw:c:\\oracle\\mw_reconcorrupt";
        var metaPath    = MetaPath(resourceKey);
        File.WriteAllText(metaPath, "not json at all");

        var report = await _locks.ReconcileAsync();

        report.CorruptMetaFilesRemoved.Should().BeGreaterThanOrEqualTo(1);
        report.Records.Should().Contain(r => r.Action == LockReconciliationAction.RemovedCorrupt);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 6. Stale .meta with dead PID is cleaned up
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stale_meta_with_dead_pid_is_cleaned_up()
    {
        var config = MakeConfig(@"C:\Oracle\MW_StaleTest");
        var resourceKey = "mw:c:\\oracle\\mw_staletest";

        // Write a stale .meta: heartbeat 8 hours ago, PID that cannot exist.
        var desc = MakeDescriptor(
            resourceKey,
            pid:       999999,          // dead PID
            heartbeat: DateTimeOffset.UtcNow.AddHours(-8));
        WriteMetaDirect(resourceKey, desc);

        // Should be cleaned during the next acquire call (which runs CleanupStale first).
        var sessionId = Guid.NewGuid();
        var result    = await _locks.TryAcquireAsync(config, sessionId);

        result.Acquired.Should().BeTrue("stale lock with dead PID must be cleaned before acquire");
        await _locks.ReleaseAsync(sessionId);
    }

    [Fact]
    public async Task CleanupStaleLocksAsync_removes_stale_meta_with_dead_pid()
    {
        var resourceKey = "mw:c:\\oracle\\mw_staleclean";
        var desc = MakeDescriptor(
            resourceKey,
            pid:       999999,
            heartbeat: DateTimeOffset.UtcNow.AddHours(-8));
        WriteMetaDirect(resourceKey, desc);

        var removed = await _locks.CleanupStaleLocksAsync(TimeSpan.FromMinutes(1));

        removed.Should().BeGreaterThanOrEqualTo(1, "stale lock must be removed");
        File.Exists(MetaPath(resourceKey)).Should().BeFalse("meta must be gone after cleanup");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 7. Active lock from live PID is kept
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Active_lock_with_live_pid_is_not_cleaned_up()
    {
        var resourceKey = "mw:c:\\oracle\\mw_active";

        // Write a lock with the current process PID and a fresh heartbeat.
        var desc = MakeDescriptor(
            resourceKey,
            pid:       Environment.ProcessId,
            heartbeat: DateTimeOffset.UtcNow);
        WriteMetaDirect(resourceKey, desc);

        // Cleanup should NOT remove this.
        var removed = await _locks.CleanupStaleLocksAsync(TimeSpan.FromMinutes(1));

        File.Exists(MetaPath(resourceKey)).Should().BeTrue(
            "lock held by live process must not be cleaned up");
        // manually clean so the test is isolated
        File.Delete(MetaPath(resourceKey));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8. Concurrent acquisition — only one caller wins per resource
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_acquisitions_only_one_wins_per_resource()
    {
        var config = MakeConfig(@"C:\Oracle\MW_Concurrent");

        // Launch many concurrent acquisition attempts for the same resource.
        const int concurrency = 20;
        var sessions = Enumerable.Range(0, concurrency).Select(_ => Guid.NewGuid()).ToList();

        var tasks = sessions.Select(s => _locks.TryAcquireAsync(config, s)).ToList();
        var results = await Task.WhenAll(tasks);

        var successes = results.Where(r => r.Acquired).ToList();
        var failures  = results.Where(r => !r.Acquired).ToList();

        successes.Should().HaveCount(1,
            "exactly one concurrent caller must win the lock");
        failures.Should().HaveCount(concurrency - 1,
            "all other callers must be blocked");

        // Clean up the winner.
        var winner = successes[0].AcquiredLocks[0].SessionId;
        await _locks.ReleaseAsync(winner);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9. All-or-nothing: partial acquisition rolls back on conflict
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Partial_acquisition_rolls_back_all_resources_on_conflict()
    {
        var config = MakeConfig(@"C:\Oracle\MW_AllOrNothing");

        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        // s1 acquires the OracleHome lock.
        var first = await _locks.TryAcquireAsync(config, s1);
        first.Acquired.Should().BeTrue();

        // s2 tries to acquire both OracleHome + Inventory. OracleHome conflicts.
        // s2 must not hold ANY resource after the attempt fails.
        var second = await _locks.TryAcquireAsync(config, s2);
        second.Acquired.Should().BeFalse("conflicts must block the whole acquire");

        // Verify no partial .meta files were left behind for s2.
        var metaFiles = Directory.GetFiles(_lockRoot, "*.meta");
        foreach (var f in metaFiles)
        {
            var json = File.ReadAllText(f);
            var desc = JsonSerializer.Deserialize<DeploymentLockDescriptor>(
                json, DeploymentJsonOptions.Create());
            desc?.SessionId.Should().NotBe(s2,
                "failed acquisition must not leave any locks for s2");
        }

        await _locks.ReleaseAsync(s1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 10. Heartbeat updates LastHeartbeatAt atomically
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_updates_LastHeartbeatAt()
    {
        var config    = MakeConfig(@"C:\Oracle\MW_Heartbeat");
        var sessionId = Guid.NewGuid();

        var acquired = await _locks.TryAcquireAsync(config, sessionId);
        acquired.Acquired.Should().BeTrue();

        // Read initial timestamp.
        var allLocks = await _locks.ListActiveLocksAsync();
        var before   = allLocks.First(l => l.SessionId == sessionId).LastHeartbeatAt;

        await Task.Delay(50); // ensure clock advances
        await _locks.HeartbeatAsync(sessionId);

        var allLocksAfter = await _locks.ListActiveLocksAsync();
        var after         = allLocksAfter.First(l => l.SessionId == sessionId).LastHeartbeatAt;

        after.Should().BeAfter(before, "heartbeat must advance LastHeartbeatAt");

        await _locks.ReleaseAsync(sessionId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 11. ReconcileAsync handles all orphan types and reports structured diagnostics
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_handles_all_orphan_types_and_returns_diagnostics()
    {
        // Plant one of each orphan type.

        // a) Stale .meta with dead PID.
        var staleKey = "mw:c:\\oracle\\mw_reconcstale";
        WriteMetaDirect(staleKey, MakeDescriptor(staleKey, pid: 999999,
            heartbeat: DateTimeOffset.UtcNow.AddHours(-8)));

        // b) Corrupt .meta.
        var corruptKey  = "mw:c:\\oracle\\mw_reconcorrupt2";
        File.WriteAllText(MetaPath(corruptKey), "{ corrupt json }}}}");

        // c) Orphaned provisional > 10 min old.
        var provSid = Guid.NewGuid();
        WriteProvisionalDirect("mw:c:\\oracle\\mw_reconcprov", provSid, "{}");
        var tmpPath = Path.Combine(_lockRoot,
            $"{SanitizeKeyForTest("mw:c:\\oracle\\mw_reconcprov")}-{provSid:N}.meta.tmp");
        File.SetLastWriteTimeUtc(tmpPath, DateTime.UtcNow.AddMinutes(-15));

        // d) Legacy v1 sentinel.
        WriteLegacySentinel("mw:c:\\oracle\\mw_reconclegacy2");

        // e) Active valid lock (should be Kept).
        var activeKey = "mw:c:\\oracle\\mw_reconcactive";
        WriteMetaDirect(activeKey, MakeDescriptor(activeKey,
            pid: Environment.ProcessId, heartbeat: DateTimeOffset.UtcNow));

        var report = await _locks.ReconcileAsync();

        report.StaleLocksRemoved.Should().BeGreaterThanOrEqualTo(1,        "stale lock removed");
        report.CorruptMetaFilesRemoved.Should().BeGreaterThanOrEqualTo(1,  "corrupt meta removed");
        report.OrphanedProvisionalsRemoved.Should().BeGreaterThanOrEqualTo(1, "orphaned provisional removed");
        report.LegacySentinelFilesRemoved.Should().BeGreaterThanOrEqualTo(1,  "legacy sentinel removed");
        report.ActiveLocksKept.Should().BeGreaterThanOrEqualTo(1,           "active lock kept");
        report.TotalActionsCount.Should().BeGreaterThanOrEqualTo(4);
        report.Records.Should().NotBeEmpty();
        report.Duration.Should().BeGreaterThan(TimeSpan.Zero);

        // Clean up the active one.
        File.Delete(MetaPath(activeKey));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 12. Cross-machine abandoned lock is cleaned up by ReconcileAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_removes_cross_machine_abandoned_lock()
    {
        var resourceKey = "mw:c:\\oracle\\mw_crossmachine";
        var staleHeartbeat = DateTimeOffset.UtcNow.AddHours(-10); // > CrossMachineAbandonMaxAge (8h)

        WriteMetaDirect(resourceKey, new DeploymentLockDescriptor
        {
            LockFileVersion = 2,
            ResourceKey     = resourceKey,
            ResourceType    = "OracleHome",
            ResourcePath    = @"C:\Oracle\MW",
            SessionId       = Guid.NewGuid(),
            MachineName     = "OTHER-MACHINE-XYZ",   // different machine
            OwnerUser       = "RemoteUser",
            OwnerProcessId  = 12345,
            AcquiredAt      = staleHeartbeat.AddMinutes(-5),
            LastHeartbeatAt = staleHeartbeat,
            DeploymentName  = "remote-deploy",
        });

        var report = await _locks.ReconcileAsync();

        report.CrossMachineAbandonedRemoved.Should().BeGreaterThanOrEqualTo(1);
        report.Records.Should().Contain(r =>
            r.Action == LockReconciliationAction.RemovedCrossMachineAbandoned);
        File.Exists(MetaPath(resourceKey)).Should().BeFalse(
            "cross-machine abandoned lock must be removed");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 13. Acquisition succeeds after releasing — .meta file lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Meta_file_is_created_on_acquire_and_removed_on_release()
    {
        var config    = MakeConfig(@"C:\Oracle\MW_LifecycleTest");
        var sessionId = Guid.NewGuid();
        var mwKey     = "mw:c:\\oracle\\mw_lifecycletest";
        var metaPath  = MetaPath(mwKey);

        File.Exists(metaPath).Should().BeFalse("no meta before acquire");

        var result = await _locks.TryAcquireAsync(config, sessionId);
        result.Acquired.Should().BeTrue();
        File.Exists(metaPath).Should().BeTrue(".meta must exist after acquire");

        // Verify the .meta contains valid JSON with correct session.
        var json = File.ReadAllText(metaPath);
        var desc = JsonSerializer.Deserialize<DeploymentLockDescriptor>(
            json, DeploymentJsonOptions.Create());
        desc.Should().NotBeNull();
        desc!.SessionId.Should().Be(sessionId);
        desc.LockFileVersion.Should().Be(2, "new acquisitions use v2 protocol");

        await _locks.ReleaseAsync(sessionId);
        File.Exists(metaPath).Should().BeFalse(".meta must be removed after release");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 14. LockFileVersion field is correct in new acquisitions
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task New_acquisition_writes_LockFileVersion_2()
    {
        var config    = MakeConfig(@"C:\Oracle\MW_VersionTest");
        var sessionId = Guid.NewGuid();
        var mwKey     = "mw:c:\\oracle\\mw_versiontest";

        (await _locks.TryAcquireAsync(config, sessionId)).Acquired.Should().BeTrue();

        var json = File.ReadAllText(MetaPath(mwKey));
        var desc = JsonSerializer.Deserialize<DeploymentLockDescriptor>(
            json, DeploymentJsonOptions.Create())!;

        desc.LockFileVersion.Should().Be(2);

        await _locks.ReleaseAsync(sessionId);
    }
}
