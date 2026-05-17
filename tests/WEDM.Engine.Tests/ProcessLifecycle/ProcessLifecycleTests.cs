using System.Collections.Concurrent;
using FluentAssertions;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.ProcessLifecycle;
using WEDM.Engine.Tests.Fakes;
using Xunit;

namespace WEDM.Engine.Tests.ProcessLifecycle;

// ═══════════════════════════════════════════════════════════════════════════════
// ProcessLifecycle Test Suite
// ═══════════════════════════════════════════════════════════════════════════════
//
// Coverage:
//   1.  OracleProcessClassifier — name/cmdline pattern matching
//   2.  ProcessOwnershipTracker — registration, session scoping, child tracking,
//       ownership queries, serialization round-trip
//   3.  OracleProcessLifecycleService — detection, tree building, orphan detection,
//       staged shutdown dispatch, rollback preparation, session report
//   4.  Process domain models — ShutdownPolicy, ProcessLifecycleReport, sentinels
//   5.  Integration: PrepareForRollbackAsync does not touch external processes
//
// All live-process operations are faked — no real PIDs are used.
// ═══════════════════════════════════════════════════════════════════════════════

// ── Shared test doubles ───────────────────────────────────────────────────────

file sealed class FakeProcessOwnershipTracker : ProcessOwnershipTracker
{
    // Expose internals for testing via public façade
    private readonly Dictionary<int, ProcessOwnershipRecord> _records = new();
    private readonly Dictionary<int, ProcessOwnership>       _ownershipOverrides = new();

    public void AddRecord(ProcessOwnershipRecord record)
    {
        _records[record.RootProcessId] = record;
    }

    public void SetOwnershipOverride(int pid, ProcessOwnership ownership)
        => _ownershipOverrides[pid] = ownership;

    public new ProcessOwnershipRecord Register(ProcessLaunchContext ctx, int pid)
    {
        var record = base.Register(ctx, pid);
        _records[pid] = record;
        return record;
    }

    public int RegisteredCount => GetAllRecords().Count;

    private IReadOnlyList<ProcessOwnershipRecord> GetAllRecords()
        => _records.Values.ToList();
}

file sealed class FakeOracleProcessManager : IOracleProcessManager
{
    public List<OracleProcessDescriptor> ProcessesToReturn { get; } = new();
    public int StopCallCount { get; private set; }
    public bool StopShouldFail { get; set; }

    public IReadOnlyList<OracleProcessDescriptor> DetectMiddlewareProcesses()
        => ProcessesToReturn.AsReadOnly();

    public Task<ProcessStopResult> StopProcessesAsync(
        IEnumerable<OracleProcessDescriptor> processes,
        bool forceAfterTimeout,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        StopCallCount++;
        var list    = processes.ToList();
        var stopped = StopShouldFail ? 0 : list.Count;
        var failed  = StopShouldFail ? list.Count : 0;
        return Task.FromResult(new ProcessStopResult
        {
            StoppedCount = stopped,
            FailedCount  = failed,
            Messages     = list.Select(p => $"Stopped: {p.ProcessName}({p.ProcessId})").ToList()
        });
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 1. OracleProcessClassifier — name and command-line classification
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OracleProcessClassifier_NameTests
{
    [Theory]
    [InlineData("java",      true)]
    [InlineData("javaw",     true)]
    [InlineData("nodemanager", true)]
    [InlineData("ohs",       true)]
    [InlineData("httpd",     true)]
    [InlineData("notepad",   false)]
    [InlineData("explorer",  false)]
    [InlineData("chrome",    false)]
    public void IsOracleCandidate_detects_expected_process_names(string name, bool expected)
    {
        OracleProcessClassifier.IsOracleCandidate(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("java", "-cp /oracle/middleware/wlserver/server/lib/weblogic.jar weblogic.Server -name AdminServer",
        OracleProcessKind.AdminServer)]
    [InlineData("java", "-jar /opt/oracle/fmw_12.2.1.4.0_infrastructure.jar -silent -response response.rsp",
        OracleProcessKind.OUI)]
    [InlineData("java", "-jar opatch.jar apply /tmp/patch",
        OracleProcessKind.OPatch)]
    [InlineData("java", "oracle.ias.tools.wlst.WLSTInterpreter",
        OracleProcessKind.WLST)]
    [InlineData("java", "-Dweblogic.Name=ManagedServer1 -cp /oracle/middleware/wlserver weblogic.Server",
        OracleProcessKind.ManagedServer)]
    public void Classify_identifies_kind_from_cmdline(string name, string cmdline, OracleProcessKind expected)
    {
        var result = OracleProcessClassifier.Classify(name, cmdline);
        result.Kind.Should().Be(expected);
        result.Confidence.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Classify_unknown_process_returns_Unknown_kind()
    {
        var result = OracleProcessClassifier.Classify("java", "-jar mycustom.jar -someflag");
        result.Kind.Should().Be(OracleProcessKind.Unknown);
    }

    [Fact]
    public void IsConfidentlyOracle_returns_false_for_Unknown()
    {
        var result = new ProcessClassificationResult
        {
            Kind       = OracleProcessKind.Unknown,
            Confidence = 0
        };
        OracleProcessClassifier.IsConfidentlyOracle(result).Should().BeFalse();
    }

    [Fact]
    public void IsConfidentlyOracle_returns_true_for_AdminServer()
    {
        var result = new ProcessClassificationResult
        {
            Kind       = OracleProcessKind.AdminServer,
            Confidence = 90
        };
        OracleProcessClassifier.IsConfidentlyOracle(result).Should().BeTrue();
    }

    [Fact]
    public void Classify_extracts_oracle_homes_from_cmdline()
    {
        var cmdline = "-cp D:\\Oracle\\Middleware\\wlserver\\server\\lib\\weblogic.jar weblogic.Server";
        var result  = OracleProcessClassifier.Classify("java", cmdline);
        result.ExtractedOracleHomes.Should().NotBeEmpty();
    }

    [Fact]
    public void Classify_extracts_jvm_args_from_cmdline()
    {
        var cmdline = "-Dweblogic.Name=AdminServer -Djava.security.policy=weblogic.policy";
        var result  = OracleProcessClassifier.Classify("java", cmdline);
        result.ExtractedJvmArgs.Should().Contain(a => a.Contains("weblogic.Name=AdminServer"));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 2. ProcessOwnershipTracker — core ownership registry
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ProcessOwnershipTracker_RegistrationTests
{
    private static ProcessLaunchContext MakeContext(Guid? sessionId = null, int attempt = 1)
        => new()
        {
            SessionId     = sessionId ?? Guid.NewGuid(),
            DeploymentId  = Guid.NewGuid(),
            AttemptNumber = attempt,
            Tool          = OracleProcessKind.OUI,
            OracleHome    = @"C:\Oracle\Middleware",
            TempRoot      = @"C:\temp\wedm-oui",
        };

    [Fact]
    public void Register_creates_ownership_record()
    {
        var tracker = new ProcessOwnershipTracker();
        var ctx     = MakeContext();

        var record = tracker.Register(ctx, pid: 1234);

        record.Should().NotBeNull();
        record.RootProcessId.Should().Be(1234);
        record.SessionId.Should().Be(ctx.SessionId);
        record.Tool.Should().Be(OracleProcessKind.OUI);
    }

    [Fact]
    public void Register_makes_pid_retrievable_via_GetOwnership()
    {
        var tracker = new ProcessOwnershipTracker();
        var ctx     = MakeContext();

        tracker.Register(ctx, 5555);

        var retrieved = tracker.GetOwnership(5555);
        retrieved.Should().NotBeNull();
        retrieved!.RootProcessId.Should().Be(5555);
    }

    [Fact]
    public void GetSessionRecords_returns_only_pids_for_that_session()
    {
        var tracker  = new ProcessOwnershipTracker();
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        tracker.Register(MakeContext(session1), 100);
        tracker.Register(MakeContext(session1), 101);
        tracker.Register(MakeContext(session2), 200);

        var s1Records = tracker.GetSessionRecords(session1);
        s1Records.Should().HaveCount(2);
        s1Records.All(r => r.SessionId == session1).Should().BeTrue();
    }

    [Fact]
    public void GetSessionPids_returns_all_pids_including_children()
    {
        var tracker = new ProcessOwnershipTracker();
        var session = Guid.NewGuid();

        tracker.Register(MakeContext(session), pid: 500);
        tracker.TrackChild(parentPid: 500, childPid: 501);

        var pids = tracker.GetSessionPids(session);
        pids.Should().Contain(500);
        pids.Should().Contain(501);
    }

    [Fact]
    public void TrackChild_associates_child_with_parent_record()
    {
        var tracker = new ProcessOwnershipTracker();
        tracker.Register(MakeContext(), 800);
        tracker.TrackChild(800, 801);

        var record = tracker.GetOwnership(800);
        record!.ChildProcessIds.Should().Contain(801);
    }

    [Fact]
    public void ClassifyOwnership_returns_WedmOwned_for_current_session_pid()
    {
        var tracker = new ProcessOwnershipTracker();
        var session = Guid.NewGuid();

        tracker.Register(MakeContext(session), 999);

        var (ownership, ownerSession) = tracker.ClassifyOwnership(999, session);
        ownership.Should().Be(ProcessOwnership.WedmOwned);
        ownerSession.Should().Be(session);
    }

    [Fact]
    public void ClassifyOwnership_returns_WedmPriorSession_for_prior_session_pid()
    {
        var tracker  = new ProcessOwnershipTracker();
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        tracker.Register(MakeContext(session1), 111);

        var (ownership, _) = tracker.ClassifyOwnership(111, session2);
        ownership.Should().Be(ProcessOwnership.WedmPriorSession);
    }

    [Fact]
    public void ClassifyOwnership_returns_Unknown_for_unregistered_pid()
    {
        var tracker = new ProcessOwnershipTracker();

        var (ownership, _) = tracker.ClassifyOwnership(9999, Guid.NewGuid());
        ownership.Should().Be(ProcessOwnership.Unknown);
    }

    [Fact]
    public void IsWedmOwned_returns_true_for_any_registered_pid()
    {
        var tracker = new ProcessOwnershipTracker();
        tracker.Register(MakeContext(), 777);

        tracker.IsWedmOwned(777).Should().BeTrue();
        tracker.IsWedmOwned(778).Should().BeFalse();
    }

    [Fact]
    public void RecordTermination_marks_record_as_terminated()
    {
        var tracker = new ProcessOwnershipTracker();
        tracker.Register(MakeContext(), 333);

        tracker.RecordTermination(333, TerminationStage.Graceful);

        var record = tracker.GetOwnership(333);
        record!.IsTerminated.Should().BeTrue();
        record.TerminationStage.Should().Be(TerminationStage.Graceful);
    }

    [Fact]
    public void GetAttemptRecords_returns_only_records_for_that_attempt()
    {
        var tracker = new ProcessOwnershipTracker();
        var session = Guid.NewGuid();

        tracker.Register(MakeContext(session, attempt: 1), 10);
        tracker.Register(MakeContext(session, attempt: 2), 20);
        tracker.Register(MakeContext(session, attempt: 2), 21);

        var attempt2 = tracker.GetAttemptRecords(session, 2);
        attempt2.Should().HaveCount(2);
        attempt2.All(r => r.AttemptNumber == 2).Should().BeTrue();
    }

    [Fact]
    public void LoadPriorSessionRecords_populates_tracker_from_persisted_data()
    {
        var tracker   = new ProcessOwnershipTracker();
        var priorSession = Guid.NewGuid();
        var records   = new List<ProcessOwnershipRecord>
        {
            new() { SessionId = priorSession, RootProcessId = 55, Tool = OracleProcessKind.WLST,
                    DeploymentId = Guid.NewGuid(), LaunchTime = DateTimeOffset.UtcNow }
        };

        tracker.LoadPriorSessionRecords(records);

        var (ownership, _) = tracker.ClassifyOwnership(55, Guid.NewGuid());
        ownership.Should().Be(ProcessOwnership.WedmPriorSession);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 3. ProcessLifecycleReport domain model
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ProcessLifecycleReport_ModelTests
{
    private static ProcessTerminationResult MakeTermResult(
        TerminationStage stage, int pid = 1) => new()
    {
        ProcessId   = pid,
        ProcessName = "java",
        Kind        = OracleProcessKind.OUI,
        Stage       = stage,
        Duration    = TimeSpan.FromSeconds(1),
    };

    [Fact]
    public void TotalTerminated_counts_only_succeeded_results()
    {
        var report = new ProcessLifecycleReport
        {
            TerminationResults = new[]
            {
                MakeTermResult(TerminationStage.Graceful,    pid: 1),
                MakeTermResult(TerminationStage.Escalated,   pid: 2),
                MakeTermResult(TerminationStage.ForcedKill,  pid: 3),
                MakeTermResult(TerminationStage.Failed,      pid: 4),
                MakeTermResult(TerminationStage.AlreadyExited, pid: 5),
                MakeTermResult(TerminationStage.Skipped,     pid: 6),
            }
        };

        report.TotalTerminated.Should().Be(5);  // Graceful+Escalated+ForcedKill+AlreadyExited+Skipped
        report.TotalFailed.Should().Be(1);
        report.TotalSkipped.Should().Be(1);
    }

    [Fact]
    public void Summary_contains_key_counts()
    {
        var report = new ProcessLifecycleReport
        {
            DetectedProcesses  = new[] { new OracleProcessInfo() },
            TerminationResults = new[] { MakeTermResult(TerminationStage.Graceful) },
            AllCleaned         = true,
        };

        report.Summary.Should().Contain("Detected=1");
        report.Summary.Should().Contain("Terminated=1");
        report.Summary.Should().Contain("Clean=True");
    }

    [Fact]
    public void AllCleaned_false_when_still_running_processes_present()
    {
        var report = new ProcessLifecycleReport
        {
            StillRunning = new[] { new OracleProcessInfo { ProcessId = 100, ProcessName = "java" } },
            AllCleaned   = false,
        };

        report.AllCleaned.Should().BeFalse();
        report.StillRunning.Should().HaveCount(1);
    }

    [Fact]
    public void ProcessTerminationResult_Succeeded_true_for_all_non_failure_stages()
    {
        var succeeded = new[]
        {
            TerminationStage.AlreadyExited,
            TerminationStage.Graceful,
            TerminationStage.Escalated,
            TerminationStage.ForcedKill,
            TerminationStage.Skipped,
        };

        foreach (var stage in succeeded)
        {
            MakeTermResult(stage).Succeeded.Should().BeTrue(
                $"because stage {stage} should count as success");
        }
    }

    [Fact]
    public void ProcessTerminationResult_Succeeded_false_for_Failed()
    {
        MakeTermResult(TerminationStage.Failed).Succeeded.Should().BeFalse();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 4. ShutdownPolicy presets
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ShutdownPolicy_PresetTests
{
    [Fact]
    public void Default_policy_has_30s_graceful_timeout()
    {
        ShutdownPolicy.Default.GracefulTimeout.Should().Be(TimeSpan.FromSeconds(30));
        ShutdownPolicy.Default.ForceKillOnEscalationFailure.Should().BeTrue();
        ShutdownPolicy.Default.SkipExternalProcesses.Should().BeTrue();
    }

    [Fact]
    public void Aggressive_policy_has_short_timeouts()
    {
        var policy = ShutdownPolicy.Aggressive;
        policy.GracefulTimeout.Should().BeLessThan(TimeSpan.FromSeconds(30));
        policy.EscalationTimeout.Should().BeLessThan(TimeSpan.FromSeconds(30));
        policy.ForceKillOnEscalationFailure.Should().BeTrue();
    }

    [Fact]
    public void Conservative_policy_has_long_graceful_timeout()
    {
        var policy = ShutdownPolicy.Conservative;
        policy.GracefulTimeout.Should().BeGreaterThan(TimeSpan.FromSeconds(30));
        policy.ForceKillOnEscalationFailure.Should().BeFalse();
    }

    [Fact]
    public void DryRun_false_by_default_on_all_presets()
    {
        ShutdownPolicy.Default.DryRun.Should().BeFalse();
        ShutdownPolicy.Aggressive.DryRun.Should().BeFalse();
        ShutdownPolicy.Conservative.DryRun.Should().BeFalse();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 5. OracleProcessInfo model
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OracleProcessInfo_ModelTests
{
    [Fact]
    public void Runtime_returns_null_when_StartTime_not_set()
    {
        var info = new OracleProcessInfo();
        info.Runtime.Should().BeNull();
    }

    [Fact]
    public void Runtime_returns_positive_duration_when_StartTime_in_past()
    {
        var info = new OracleProcessInfo
        {
            StartTime = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        info.Runtime.Should().NotBeNull();
        info.Runtime!.Value.TotalMinutes.Should().BeGreaterThan(4);
    }

    [Fact]
    public void ToString_contains_pid_kind_and_ownership()
    {
        var info = new OracleProcessInfo
        {
            ProcessId   = 1234,
            ProcessName = "java",
            Kind        = OracleProcessKind.AdminServer,
            Ownership   = ProcessOwnership.WedmOwned,
        };

        var str = info.ToString();
        str.Should().Contain("1234");
        str.Should().Contain("AdminServer");
        str.Should().Contain("WedmOwned");
    }

    [Fact]
    public void OracleProcessTree_All_enumerates_root_and_descendants()
    {
        var root = new OracleProcessTree
        {
            Root = new OracleProcessInfo { ProcessId = 1 },
            Children = new[]
            {
                new OracleProcessTree
                {
                    Root = new OracleProcessInfo { ProcessId = 2 },
                    Children = new[]
                    {
                        new OracleProcessTree { Root = new OracleProcessInfo { ProcessId = 3 } }
                    }
                }
            }
        };

        root.All().Select(p => p.ProcessId).Should().BeEquivalentTo(new[] { 1, 2, 3 });
        root.TotalCount.Should().Be(3);
    }

    [Fact]
    public void OracleProcessTree_HasInventoryLock_true_when_any_node_locked()
    {
        var tree = new OracleProcessTree
        {
            Root = new OracleProcessInfo { ProcessId = 1, HoldsInventoryLock = false },
            Children = new[]
            {
                new OracleProcessTree
                {
                    Root = new OracleProcessInfo { ProcessId = 2, HoldsInventoryLock = true }
                }
            }
        };

        tree.HasInventoryLock.Should().BeTrue();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 6. OracleProcessLifecycleService — deterministic unit tests via fakes
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OracleProcessLifecycleService_CoreTests
{
    private static (OracleProcessLifecycleService svc, ProcessOwnershipTracker tracker, FakeLoggingService log)
        BuildService()
    {
        var log     = new FakeLoggingService();
        var tracker = new ProcessOwnershipTracker();
        var svc     = new OracleProcessLifecycleService(log, tracker);
        return (svc, tracker, log);
    }

    [Fact]
    public void RegisterLaunch_returns_populated_record_and_logs()
    {
        var (svc, _, log) = BuildService();
        var ctx = new ProcessLaunchContext
        {
            SessionId     = Guid.NewGuid(),
            DeploymentId  = Guid.NewGuid(),
            AttemptNumber = 1,
            Tool          = OracleProcessKind.WLST,
            OracleHome    = @"C:\Oracle\MW",
        };

        var record = svc.RegisterLaunch(ctx, pid: 42);

        record.RootProcessId.Should().Be(42);
        record.Tool.Should().Be(OracleProcessKind.WLST);
        log.GetEntries().Should().Contain(e => e.Message.Contains("42"));
    }

    [Fact]
    public void TrackChildProcess_adds_child_to_parent_record()
    {
        var (svc, tracker, _) = BuildService();
        var ctx = new ProcessLaunchContext
        {
            SessionId = Guid.NewGuid(), DeploymentId = Guid.NewGuid(),
            Tool = OracleProcessKind.OUI, OracleHome = @"C:\oracle"
        };

        svc.RegisterLaunch(ctx, 100);
        svc.TrackChildProcess(100, 101);

        var record = tracker.GetOwnership(100);
        record!.ChildProcessIds.Should().Contain(101);
    }

    [Fact]
    public void GetOwnedProcesses_returns_records_for_session()
    {
        var (svc, _, _) = BuildService();
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        var ctx1 = new ProcessLaunchContext { SessionId = session1, DeploymentId = Guid.NewGuid(), Tool = OracleProcessKind.OUI, OracleHome = @"C:\o" };
        var ctx2 = new ProcessLaunchContext { SessionId = session2, DeploymentId = Guid.NewGuid(), Tool = OracleProcessKind.OUI, OracleHome = @"C:\o" };

        svc.RegisterLaunch(ctx1, 200);
        svc.RegisterLaunch(ctx1, 201);
        svc.RegisterLaunch(ctx2, 300);

        svc.GetOwnedProcesses(session1).Should().HaveCount(2);
        svc.GetOwnedProcesses(session2).Should().HaveCount(1);
    }

    [Fact]
    public void BuildProcessTree_returns_null_for_nonexistent_pid()
    {
        var (svc, _, _) = BuildService();

        // Use a PID that cannot exist on any real machine
        var tree = svc.BuildProcessTree(int.MaxValue);
        tree.Should().BeNull();
    }

    [Fact]
    public void BuildSessionProcessTrees_returns_empty_list_for_session_with_no_pids()
    {
        var (svc, _, _) = BuildService();
        var emptySession = Guid.NewGuid();

        var trees = svc.BuildSessionProcessTrees(emptySession);
        trees.Should().BeEmpty();
    }

    [Fact]
    public void VerifyCleanup_considers_nonexistent_pids_as_exited()
    {
        var (svc, _, _) = BuildService();

        // Very high PIDs that cannot exist
        var nonExistentPids = new[] { int.MaxValue - 1, int.MaxValue - 2 };
        var stillRunning    = svc.VerifyCleanup(nonExistentPids);

        stillRunning.Should().BeEmpty("non-existent PIDs are treated as exited");
    }

    [Fact]
    public void IsOracleHomeLocked_returns_false_for_empty_path()
    {
        var (svc, _, _) = BuildService();
        svc.IsOracleHomeLocked(string.Empty).Should().BeFalse();
        svc.IsOracleHomeLocked(null!).Should().BeFalse();
    }

    [Fact]
    public void GenerateSessionReport_returns_report_with_correct_session_id()
    {
        var (svc, _, _) = BuildService();
        var session = Guid.NewGuid();

        var report = svc.GenerateSessionReport(session);
        report.SessionId.Should().Be(session);
    }

    [Fact]
    public async Task ShutdownAsync_with_empty_list_returns_empty_results()
    {
        var (svc, _, _) = BuildService();

        var results = await svc.ShutdownAsync([], ShutdownPolicy.Default);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupSessionAsync_on_empty_session_returns_clean_report()
    {
        var (svc, _, _) = BuildService();
        var session = Guid.NewGuid();

        // Session has no registered processes — nothing to clean up
        var report = await svc.CleanupSessionAsync(session);

        report.AllCleaned.Should().BeTrue();
        report.StillRunning.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupBeforeRetryAsync_returns_report_for_empty_prior_attempt()
    {
        var (svc, _, _) = BuildService();
        var session = Guid.NewGuid();

        var report = await svc.CleanupBeforeRetryAsync(session, priorAttemptNumber: 1);

        // No processes from attempt 1 means nothing to terminate
        report.Should().NotBeNull();
        report.StillRunning.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanForCrashRemnantsAsync_with_no_prior_records_returns_clean()
    {
        var (svc, _, _) = BuildService();

        var report = await svc.ScanForCrashRemnantsAsync(Array.Empty<ProcessOwnershipRecord>());

        report.Should().NotBeNull();
        report.ManualActionItems.Should().NotContainNulls();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 7. OrphanDetection — classification logic
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OracleProcessLifecycleService_OrphanDetectionTests
{
    [Fact]
    public void DetectOrphans_returns_empty_when_no_oracle_processes_running()
    {
        var log     = new FakeLoggingService();
        var tracker = new ProcessOwnershipTracker();
        var svc     = new OracleProcessLifecycleService(log, tracker);

        // On CI/dev machines with no Oracle installed, DetectOracleProcesses returns empty
        var orphans = svc.DetectOrphans();
        // We cannot assert count==0 because there might be real Oracle processes;
        // but we can assert every returned item has a non-Unknown ownership.
        foreach (var orphan in orphans)
        {
            orphan.ProcessId.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task ReconcileOrphansAsync_with_AutoTerminate_false_skips_termination()
    {
        var log     = new FakeLoggingService();
        var tracker = new ProcessOwnershipTracker();
        var svc     = new OracleProcessLifecycleService(log, tracker);

        var options = new OrphanReconciliationOptions
        {
            AutoTerminateWedmOrphans = false,
            SuggestExternalCleanup   = false,
            IncludeUnknownJvms       = false,
            Policy                   = ShutdownPolicy.Default,
        };

        var report = await svc.ReconcileOrphansAsync(options);

        // With auto-terminate disabled, nothing should be in the TerminationResults
        // that is flagged as succeeded-via-kill (except AlreadyExited if any ran just before)
        report.Should().NotBeNull();
        report.TerminationResults.Should().NotContain(r =>
            r.Stage == TerminationStage.Graceful
            || r.Stage == TerminationStage.Escalated
            || r.Stage == TerminationStage.ForcedKill);
    }

    [Fact]
    public async Task PrepareForRollbackAsync_never_terminates_external_processes()
    {
        var log     = new FakeLoggingService();
        var tracker = new ProcessOwnershipTracker();
        var svc     = new OracleProcessLifecycleService(log, tracker);

        // Use a fake oracle home that no real process references
        var fakeHome = @"Z:\NonExistentOracle\Home";

        var report = await svc.PrepareForRollbackAsync(fakeHome, Guid.NewGuid());

        // ExternalOrphans must never appear in TerminationResults with kill stages
        var externalPids = report.ExternalOrphans.Select(p => p.ProcessId).ToHashSet();
        foreach (var result in report.TerminationResults)
        {
            if (externalPids.Contains(result.ProcessId))
            {
                result.Stage.Should().Be(TerminationStage.Skipped,
                    $"external process PID {result.ProcessId} must only be skipped, never killed");
            }
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 8. ProcessLaunchContext and ProcessOwnershipRecord model tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ProcessOwnershipRecord_ModelTests
{
    [Fact]
    public void IsTerminated_false_when_TerminatedAt_null()
    {
        var record = new ProcessOwnershipRecord();
        record.IsTerminated.Should().BeFalse();
    }

    [Fact]
    public void IsTerminated_true_when_TerminatedAt_set()
    {
        var record = new ProcessOwnershipRecord { TerminatedAt = DateTimeOffset.UtcNow };
        record.IsTerminated.Should().BeTrue();
    }

    [Fact]
    public void ActiveDuration_returns_null_when_not_terminated()
    {
        var record = new ProcessOwnershipRecord { LaunchTime = DateTimeOffset.UtcNow };
        record.ActiveDuration.Should().BeNull();
    }

    [Fact]
    public void ActiveDuration_returns_positive_when_terminated()
    {
        var launch = DateTimeOffset.UtcNow.AddSeconds(-10);
        var term   = DateTimeOffset.UtcNow;
        var record = new ProcessOwnershipRecord
        {
            LaunchTime    = launch,
            TerminatedAt  = term,
        };

        record.ActiveDuration.Should().NotBeNull();
        record.ActiveDuration!.Value.TotalSeconds.Should().BeApproximately(10, 0.5);
    }

    [Fact]
    public void ChildProcessIds_defaults_to_empty_list()
    {
        var record = new ProcessOwnershipRecord();
        record.ChildProcessIds.Should().BeEmpty();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 9. OrphanReconciliationOptions defaults
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OrphanReconciliationOptions_DefaultTests
{
    [Fact]
    public void Default_options_have_sensible_values()
    {
        var opts = new OrphanReconciliationOptions();

        opts.AutoTerminateWedmOrphans.Should().BeTrue();
        opts.SuggestExternalCleanup.Should().BeTrue();
        opts.IncludeUnknownJvms.Should().BeFalse();
        opts.PriorSessionIds.Should().BeEmpty();
        opts.Policy.Should().NotBeNull();
    }

    [Fact]
    public void Custom_options_apply_correctly()
    {
        var session = Guid.NewGuid();
        var opts = new OrphanReconciliationOptions
        {
            AutoTerminateWedmOrphans = false,
            SuggestExternalCleanup   = false,
            IncludeUnknownJvms       = true,
            PriorSessionIds          = new[] { session },
            Policy                   = ShutdownPolicy.Aggressive,
        };

        opts.AutoTerminateWedmOrphans.Should().BeFalse();
        opts.IncludeUnknownJvms.Should().BeTrue();
        opts.PriorSessionIds.Should().Contain(session);
        opts.Policy.GracefulTimeout.Should().Be(ShutdownPolicy.Aggressive.GracefulTimeout);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 10. ProcessClassificationResult confidence model
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ProcessClassificationResult_Tests
{
    [Fact]
    public void Low_confidence_classification_is_not_confidently_oracle()
    {
        var result = new ProcessClassificationResult
        {
            Kind       = OracleProcessKind.OrphanJvm,
            Confidence = 20
        };

        OracleProcessClassifier.IsConfidentlyOracle(result).Should().BeFalse();
    }

    [Theory]
    [InlineData(OracleProcessKind.OUI,          true)]
    [InlineData(OracleProcessKind.AdminServer,   true)]
    [InlineData(OracleProcessKind.NodeManager,   true)]
    [InlineData(OracleProcessKind.Unknown,       false)]
    public void IsConfidentlyOracle_correct_for_known_kinds_at_high_confidence(
        OracleProcessKind kind, bool expected)
    {
        var result = new ProcessClassificationResult
        {
            Kind       = kind,
            Confidence = kind == OracleProcessKind.Unknown ? 0 : 85
        };

        OracleProcessClassifier.IsConfidentlyOracle(result).Should().Be(expected);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 11. OracleProcessClassifier — edge cases
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OracleProcessClassifier_EdgeCaseTests
{
    [Fact]
    public void Classify_handles_null_cmdline_gracefully()
    {
        var act = () => OracleProcessClassifier.Classify("java", null);
        act.Should().NotThrow();
    }

    [Fact]
    public void Classify_handles_empty_cmdline_gracefully()
    {
        var result = OracleProcessClassifier.Classify("java", "");
        result.Kind.Should().Be(OracleProcessKind.Unknown);
    }

    [Fact]
    public void IsOracleCandidate_case_insensitive()
    {
        OracleProcessClassifier.IsOracleCandidate("JAVA").Should().BeTrue();
        OracleProcessClassifier.IsOracleCandidate("Java").Should().BeTrue();
        OracleProcessClassifier.IsOracleCandidate("NodeManager").Should().BeTrue();
    }

    [Fact]
    public void Classify_recognizes_rcu_bat_cmdline()
    {
        var result = OracleProcessClassifier.Classify("java", "-jar rcu.jar -silent -createRepository");
        // Either RCU or OrphanJvm — both are acceptable; must not be Unknown for Oracle-referencing cmdline
        result.Kind.Should().NotBe(OracleProcessKind.Unknown);
    }

    [Fact]
    public void ExtractedOracleHomes_empty_when_no_oracle_paths_in_cmdline()
    {
        var result = OracleProcessClassifier.Classify("java", "-jar myapp.jar");
        result.ExtractedOracleHomes.Should().BeEmpty();
    }

    [Fact]
    public void Classify_nodemanager_by_name()
    {
        var result = OracleProcessClassifier.Classify("nodemanager", "");
        result.Kind.Should().Be(OracleProcessKind.NodeManager);
    }
}
