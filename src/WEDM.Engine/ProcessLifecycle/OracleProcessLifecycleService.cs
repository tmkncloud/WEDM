using System.Diagnostics;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.ProcessLifecycle;

/// <summary>
/// Full Oracle-aware process lifecycle management service.
/// Implements <see cref="IOracleProcessLifecycleService"/>.
///
/// Staged shutdown sequence (per process):
///   Stage 1 — Graceful : send WM_CLOSE / CloseMainWindow.
///   Stage 2 — Wait     : poll until graceful timeout expires.
///   Stage 3 — Escalate : send SIGTERM via taskkill /F /PID.
///   Stage 4 — ForceKill: Process.Kill(entireProcessTree: true).
///
/// External process protection:
///   Any process whose ownership is <see cref="ProcessOwnership.External"/> is
///   NEVER terminated automatically. It receives <see cref="TerminationStage.Skipped"/>.
///
/// Thread-safety: the service is designed as a singleton. The <see cref="ProcessOwnershipTracker"/>
/// handles its own locking. Process detection methods enumerate live system state on each call.
/// </summary>
public sealed class OracleProcessLifecycleService : IOracleProcessLifecycleService
{
    private readonly ILoggingService         _log;
    private readonly ProcessOwnershipTracker _tracker;

    // Tracks termination results per session for reporting
    private readonly Dictionary<Guid, List<ProcessTerminationResult>> _terminationHistory = new();
    private readonly object _historyLock = new();

    public OracleProcessLifecycleService(ILoggingService log, ProcessOwnershipTracker tracker)
    {
        _log     = log;
        _tracker = tracker;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Launch registration
    // ─────────────────────────────────────────────────────────────────────────

    public ProcessOwnershipRecord RegisterLaunch(ProcessLaunchContext context, int pid)
    {
        var record = _tracker.Register(context, pid);
        _log.Info(
            $"[ProcessLifecycle] Registered launch: {context.Tool} PID={pid} " +
            $"session={context.SessionId:N} attempt={context.AttemptNumber}",
            "ProcessLifecycle");
        return record;
    }

    public void TrackChildProcess(int parentPid, int childPid)
    {
        _tracker.TrackChild(parentPid, childPid);
        _log.Info(
            $"[ProcessLifecycle] Tracked child PID={childPid} under parent PID={parentPid}",
            "ProcessLifecycle");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Process detection
    // ─────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<OracleProcessInfo> DetectOracleProcesses()
    {
        var results = new List<OracleProcessInfo>();
        var snapshot = ProcessTreeBuilder.SnapshotAllProcesses();

        foreach (var entry in snapshot.Values)
        {
            if (!OracleProcessClassifier.IsOracleCandidate(
                    entry.Name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)))
                continue;

            var classification = OracleProcessClassifier.Classify(entry.Name, entry.CommandLine);
            if (!OracleProcessClassifier.IsConfidentlyOracle(classification))
                continue;

            var (ownership, ownerSession) = _tracker.ClassifyOwnership(entry.Pid, Guid.Empty);
            var ownerRecord = _tracker.GetOwnership(entry.Pid);

            DateTimeOffset? startTime = null;
            try
            {
                using var proc = Process.GetProcessById(entry.Pid);
                startTime = new DateTimeOffset(proc.StartTime.ToUniversalTime());
            }
            catch { }

            results.Add(new OracleProcessInfo
            {
                ProcessId            = entry.Pid,
                ParentProcessId      = entry.Ppid,
                ProcessName          = entry.Name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
                Kind                 = classification.Kind,
                Ownership            = ownership,
                CommandLine          = entry.CommandLine,
                DetectedJvmArgs      = classification.ExtractedJvmArgs,
                OracleHomeRefs       = classification.ExtractedOracleHomes,
                ClassificationReason = classification.Reason,
                StartTime            = startTime,
                OwnerSessionId       = ownerSession,
                OwnerAttemptNumber   = ownerRecord?.AttemptNumber,
            });
        }

        _log.Info($"[ProcessLifecycle] DetectOracleProcesses: {results.Count} Oracle process(es) found.", "ProcessLifecycle");
        return results.AsReadOnly();
    }

    public IReadOnlyList<ProcessOwnershipRecord> GetOwnedProcesses(Guid sessionId)
        => _tracker.GetSessionRecords(sessionId);

    // ─────────────────────────────────────────────────────────────────────────
    // Process tree management
    // ─────────────────────────────────────────────────────────────────────────

    public OracleProcessTree? BuildProcessTree(int rootPid)
    {
        var ownerRecord = _tracker.GetOwnership(rootPid);
        return ProcessTreeBuilder.Build(rootPid, ownerRecord);
    }

    public IReadOnlyList<OracleProcessTree> BuildSessionProcessTrees(Guid sessionId)
    {
        var pids = _tracker.GetSessionPids(sessionId);
        var ownership = pids
            .Select(pid => _tracker.GetOwnership(pid))
            .Where(r => r is not null)
            .Cast<ProcessOwnershipRecord>()
            .ToDictionary(r => r.RootProcessId);
        return ProcessTreeBuilder.BuildMany(pids, ownership);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Staged shutdown
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProcessTerminationResult>> ShutdownAsync(
        IEnumerable<OracleProcessInfo> processes,
        ShutdownPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ProcessTerminationResult>();

        foreach (var proc in processes.DistinctBy(p => p.ProcessId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await TerminateSingleAsync(proc, policy, cancellationToken).ConfigureAwait(false);
            results.Add(result);

            _log.Info($"[ProcessLifecycle] {result}", "ProcessLifecycle");
        }

        return results.AsReadOnly();
    }

    public async Task<ProcessLifecycleReport> CleanupSessionAsync(
        Guid sessionId,
        ShutdownPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= ShutdownPolicy.Default;
        _log.Info($"[ProcessLifecycle] CleanupSession {sessionId:N} starting.", "ProcessLifecycle");

        // 1. Detect all Oracle processes and filter to session-owned ones
        var allOracle  = DetectOracleProcesses();
        var sessionOwned = allOracle
            .Where(p => p.OwnerSessionId == sessionId
                     || _tracker.IsWedmOwned(p.ProcessId))
            .ToList();

        // 2. Also include any registered PIDs that may no longer be in the detection scan
        var registeredPids = _tracker.GetSessionPids(sessionId);
        var additionalPids = registeredPids.Except(sessionOwned.Select(p => p.ProcessId));
        foreach (var pid in additionalPids)
        {
            var info = ProcessTreeBuilder.BuildProcessInfo(pid, _tracker.GetOwnership(pid));
            if (info is not null) sessionOwned.Add(info);
        }

        // 3. Terminate
        var termResults = await ShutdownAsync(sessionOwned, policy, cancellationToken).ConfigureAwait(false);

        // 4. Verify
        var stillRunning = VerifyCleanup(sessionOwned.Select(p => p.ProcessId));

        RecordHistory(sessionId, termResults);

        var report = new ProcessLifecycleReport
        {
            SessionId          = sessionId,
            DetectedProcesses  = allOracle,
            TerminationResults = termResults,
            StillRunning       = stillRunning,
            AllCleaned         = stillRunning.Count == 0,
        };

        _log.Info($"[ProcessLifecycle] CleanupSession complete: {report.Summary}", "ProcessLifecycle");
        return report;
    }

    public async Task<ProcessLifecycleReport> CleanupBeforeRetryAsync(
        Guid sessionId,
        int priorAttemptNumber,
        ShutdownPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= ShutdownPolicy.Aggressive;
        _log.Info(
            $"[ProcessLifecycle] CleanupBeforeRetry: session={sessionId:N} priorAttempt={priorAttemptNumber}",
            "ProcessLifecycle");

        // Find all processes from the prior attempt
        var priorRecords = _tracker.GetAttemptRecords(sessionId, priorAttemptNumber);
        var priorPids    = priorRecords.Select(r => r.RootProcessId)
            .Concat(priorRecords.SelectMany(r => r.ChildProcessIds))
            .Distinct()
            .ToList();

        // Build process infos
        var processInfos = new List<OracleProcessInfo>();
        foreach (var pid in priorPids)
        {
            var info = ProcessTreeBuilder.BuildProcessInfo(pid, _tracker.GetOwnership(pid));
            if (info is not null)
                processInfos.Add(info with { Ownership = ProcessOwnership.WedmPriorSession });
        }

        // Also scan for any unregistered Oracle processes that look like OUI/WLST leftovers
        var allOracle = DetectOracleProcesses();
        var unregisteredOrphans = allOracle
            .Where(p => p.Ownership == ProcessOwnership.Unknown
                     && p.Kind is OracleProcessKind.OUI or OracleProcessKind.WLST
                            or OracleProcessKind.OPatch or OracleProcessKind.OrphanJvm)
            .ToList();
        processInfos.AddRange(unregisteredOrphans);

        var termResults = await ShutdownAsync(processInfos, policy, cancellationToken).ConfigureAwait(false);
        var stillRunning = VerifyCleanup(processInfos.Select(p => p.ProcessId));

        RecordHistory(sessionId, termResults);

        return new ProcessLifecycleReport
        {
            SessionId          = sessionId,
            DetectedProcesses  = processInfos.AsReadOnly(),
            TerminationResults = termResults,
            StillRunning       = stillRunning,
            AllCleaned         = stillRunning.Count == 0,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Orphan detection and reconciliation
    // ─────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<OracleProcessInfo> DetectOrphans(
        IEnumerable<Guid>? priorSessionIds = null)
    {
        var priorSessions = priorSessionIds?.ToHashSet() ?? new HashSet<Guid>();
        var allOracle     = DetectOracleProcesses();
        var orphans       = new List<OracleProcessInfo>();

        foreach (var proc in allOracle)
        {
            if (proc.Ownership == ProcessOwnership.WedmOwned)
                continue; // current-session process — not an orphan

            if (proc.Ownership == ProcessOwnership.External)
                continue; // external — never an orphan candidate

            // Prior-session ownership or unknown
            if (proc.Ownership == ProcessOwnership.WedmPriorSession
             || (proc.Ownership == ProcessOwnership.Unknown
                 && proc.Kind != OracleProcessKind.Unknown))
            {
                orphans.Add(proc);
            }
        }

        _log.Info($"[ProcessLifecycle] DetectOrphans: {orphans.Count} orphan candidate(s) found.", "ProcessLifecycle");
        return orphans.AsReadOnly();
    }

    public async Task<ProcessLifecycleReport> ReconcileOrphansAsync(
        OrphanReconciliationOptions options,
        CancellationToken cancellationToken = default)
    {
        _log.Info("[ProcessLifecycle] Reconciling orphans.", "ProcessLifecycle");

        var orphans          = DetectOrphans(options.PriorSessionIds);
        var wedmOrphans      = orphans.Where(p => p.Ownership == ProcessOwnership.WedmPriorSession).ToList();
        var externalOrphans  = orphans.Where(p => p.Ownership == ProcessOwnership.External).ToList();
        var unknownOrphans   = orphans.Where(p => p.Ownership == ProcessOwnership.Unknown).ToList();

        var termResults      = new List<ProcessTerminationResult>();
        var reconciled       = new List<OracleProcessInfo>();
        var manualActions    = new List<string>();

        // 1. Terminate WEDM-owned orphans from prior sessions
        if (options.AutoTerminateWedmOrphans && wedmOrphans.Count > 0)
        {
            var results = await ShutdownAsync(wedmOrphans, options.Policy, cancellationToken).ConfigureAwait(false);
            termResults.AddRange(results);
            reconciled.AddRange(wedmOrphans.Where(p => results.Any(r => r.ProcessId == p.ProcessId && r.Succeeded)));
        }

        // 2. Include unknown orphans if configured
        if (options.IncludeUnknownJvms && unknownOrphans.Count > 0)
        {
            var results = await ShutdownAsync(unknownOrphans, options.Policy, cancellationToken).ConfigureAwait(false);
            termResults.AddRange(results);
        }

        // 3. Report external orphans — NEVER terminate
        foreach (var ext in externalOrphans)
        {
            manualActions.Add(
                $"External Oracle process detected: {ext.ProcessName} (PID={ext.ProcessId}, Kind={ext.Kind}). " +
                $"Review and stop manually if safe. Reason: {ext.ClassificationReason}");
        }

        if (options.SuggestExternalCleanup && externalOrphans.Count > 0)
        {
            _log.Warning(
                $"[ProcessLifecycle] {externalOrphans.Count} external Oracle process(es) detected. " +
                "These are NOT terminated automatically.",
                "ProcessLifecycle");
        }

        var allStillRunning = VerifyCleanup(
            wedmOrphans.Concat(unknownOrphans).Select(p => p.ProcessId));

        return new ProcessLifecycleReport
        {
            SessionId          = Guid.Empty,
            DetectedProcesses  = orphans,
            TerminationResults = termResults.AsReadOnly(),
            OrphansFound       = orphans,
            OrphansReconciled  = reconciled.AsReadOnly(),
            ExternalOrphans    = externalOrphans.AsReadOnly(),
            ManualActionItems  = manualActions.AsReadOnly(),
            StillRunning       = allStillRunning,
            AllCleaned         = allStillRunning.Count == 0 && externalOrphans.Count == 0,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Crash recovery
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<ProcessLifecycleReport> ScanForCrashRemnantsAsync(
        IEnumerable<ProcessOwnershipRecord> priorSessionRecords,
        CancellationToken cancellationToken = default)
    {
        _log.Info("[ProcessLifecycle] Scanning for crash remnants from prior sessions.", "ProcessLifecycle");

        // Load prior records so ownership classification works correctly
        _tracker.LoadPriorSessionRecords(priorSessionRecords);

        var options = new OrphanReconciliationOptions
        {
            AutoTerminateWedmOrphans = false, // crash recovery only scans; doesn't auto-terminate
            SuggestExternalCleanup   = true,
            IncludeUnknownJvms       = true,
            Policy                   = ShutdownPolicy.Conservative,
        };

        return await ReconcileOrphansAsync(options, cancellationToken).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rollback integration
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<ProcessLifecycleReport> PrepareForRollbackAsync(
        string oracleHome,
        Guid sessionId,
        ShutdownPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= ShutdownPolicy.Aggressive;
        _log.Info(
            $"[ProcessLifecycle] PrepareForRollback: oracleHome={oracleHome} session={sessionId:N}",
            "ProcessLifecycle");

        var allOracle = DetectOracleProcesses();

        // Classify by rollback priority: processes that lock the oracle home take precedence
        var rollbackTargets = allOracle
            .Where(p =>
                // Session-owned processes
                p.OwnerSessionId == sessionId
                || _tracker.IsWedmOwned(p.ProcessId)
                // Processes referencing the target Oracle home
                || p.OracleHomeRefs.Any(h => h.StartsWith(oracleHome, StringComparison.OrdinalIgnoreCase))
                // Known short-lived tools that WEDM launches (OUI, WLST, OPatch)
                || p.Kind is OracleProcessKind.OUI or OracleProcessKind.WLST or OracleProcessKind.OPatch
                          or OracleProcessKind.NodeManager or OracleProcessKind.FormsRuntime
                          or OracleProcessKind.ReportsRuntime or OracleProcessKind.OHS)
            .Where(p => p.Ownership != ProcessOwnership.External) // never touch external
            .ToList();

        _log.Info(
            $"[ProcessLifecycle] Rollback targets: {rollbackTargets.Count} process(es).",
            "ProcessLifecycle");

        var termResults  = await ShutdownAsync(rollbackTargets, policy, cancellationToken).ConfigureAwait(false);
        var stillRunning = VerifyCleanup(rollbackTargets.Select(p => p.ProcessId));
        var homeLocked   = IsOracleHomeLocked(oracleHome);

        if (homeLocked)
            _log.Warning(
                $"[ProcessLifecycle] Oracle Home '{oracleHome}' is still locked after process cleanup.",
                "ProcessLifecycle");

        RecordHistory(sessionId, termResults);

        var manualActions = new List<string>();
        if (homeLocked)
            manualActions.Add($"Oracle Home '{oracleHome}' still has open file handles. Check for external processes.");

        return new ProcessLifecycleReport
        {
            SessionId          = sessionId,
            DetectedProcesses  = allOracle,
            TerminationResults = termResults,
            StillRunning       = stillRunning,
            AllCleaned         = stillRunning.Count == 0 && !homeLocked,
            ManualActionItems  = manualActions.AsReadOnly(),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Verification
    // ─────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<OracleProcessInfo> VerifyCleanup(IEnumerable<int> pids)
    {
        var stillRunning = new List<OracleProcessInfo>();

        foreach (var pid in pids.Distinct())
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    var info = ProcessTreeBuilder.BuildProcessInfo(pid);
                    if (info is not null)
                        stillRunning.Add(info);
                }
            }
            catch (ArgumentException)
            {
                // Process no longer exists — this is success
            }
            catch
            {
                // Cannot read process state — assume exited
            }
        }

        return stillRunning.AsReadOnly();
    }

    public bool IsOracleHomeLocked(string oracleHome)
    {
        if (string.IsNullOrWhiteSpace(oracleHome)) return false;

        // Check if any detected Oracle process has an open reference to the oracle home
        var allOracle = DetectOracleProcesses();
        return allOracle.Any(p =>
            p.OracleHomeRefs.Any(h => h.StartsWith(oracleHome, StringComparison.OrdinalIgnoreCase))
            && p.Ownership != ProcessOwnership.WedmOwned); // exclude our own processes that we're about to kill
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Diagnostics
    // ─────────────────────────────────────────────────────────────────────────

    public ProcessLifecycleReport GenerateSessionReport(Guid sessionId)
    {
        var detected   = DetectOracleProcesses();
        var owned      = _tracker.GetSessionRecords(sessionId);
        var ownedPids  = owned.Select(r => r.RootProcessId).ToHashSet();
        var sessionProcs = detected.Where(p => ownedPids.Contains(p.ProcessId)).ToList();
        var orphans    = DetectOrphans();

        List<ProcessTerminationResult> history;
        lock (_historyLock)
        {
            _terminationHistory.TryGetValue(sessionId, out var h);
            history = h ?? [];
        }

        return new ProcessLifecycleReport
        {
            SessionId          = sessionId,
            DetectedProcesses  = detected,
            TerminationResults = history.AsReadOnly(),
            OrphansFound       = orphans,
            AllCleaned         = sessionProcs.Count == 0,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Staged termination implementation
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ProcessTerminationResult> TerminateSingleAsync(
        OracleProcessInfo target,
        ShutdownPolicy policy,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        // Guard: never automatically terminate external processes
        if (target.Ownership == ProcessOwnership.External && policy.SkipExternalProcesses)
        {
            _log.Warning(
                $"[ProcessLifecycle] Skipping external process: {target} (ExternalOwnership guard).",
                "ProcessLifecycle");
            return TermResult(target, TerminationStage.Skipped, sw.Elapsed);
        }

        Process? proc;
        try { proc = Process.GetProcessById(target.ProcessId); }
        catch (ArgumentException)
        {
            _tracker.RecordTermination(target.ProcessId, TerminationStage.AlreadyExited);
            return TermResult(target, TerminationStage.AlreadyExited, sw.Elapsed);
        }

        using (proc)
        {
            if (proc.HasExited)
            {
                _tracker.RecordTermination(target.ProcessId, TerminationStage.AlreadyExited);
                return TermResult(target, TerminationStage.AlreadyExited, sw.Elapsed);
            }

            if (policy.DryRun)
            {
                _log.Info($"[ProcessLifecycle] DryRun — would terminate: {target}", "ProcessLifecycle");
                return TermResult(target, TerminationStage.Skipped, sw.Elapsed);
            }

            // Stage 1: Graceful close
            try { proc.CloseMainWindow(); } catch { }

            // Stage 2: Wait for graceful exit
            var graceful = await WaitForExitAsync(proc, policy.GracefulTimeout, cancellationToken).ConfigureAwait(false);
            if (graceful)
            {
                _tracker.RecordTermination(target.ProcessId, TerminationStage.Graceful);
                return TermResult(target, TerminationStage.Graceful, sw.Elapsed);
            }

            // Stage 3: Escalation via taskkill
            try
            {
                using var taskkill = Process.Start(new ProcessStartInfo
                {
                    FileName  = "taskkill",
                    Arguments = $"/F /PID {target.ProcessId}",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                await (taskkill?.WaitForExitAsync(cancellationToken) ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch { }

            var escalated = await WaitForExitAsync(proc, policy.EscalationTimeout, cancellationToken).ConfigureAwait(false);
            if (escalated)
            {
                _tracker.RecordTermination(target.ProcessId, TerminationStage.Escalated);
                return TermResult(target, TerminationStage.Escalated, sw.Elapsed);
            }

            // Stage 4: Force kill entire process tree
            if (policy.ForceKillOnEscalationFailure)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    var killed = await WaitForExitAsync(proc, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    if (killed)
                    {
                        _tracker.RecordTermination(target.ProcessId, TerminationStage.ForcedKill);
                        return TermResult(target, TerminationStage.ForcedKill, sw.Elapsed);
                    }
                }
                catch (Exception ex)
                {
                    _tracker.RecordTermination(target.ProcessId, TerminationStage.Failed);
                    return TermResult(target, TerminationStage.Failed, sw.Elapsed,
                        $"Force kill failed: {ex.Message}");
                }
            }

            _tracker.RecordTermination(target.ProcessId, TerminationStage.Failed);
            return TermResult(target, TerminationStage.Failed, sw.Elapsed,
                "Process did not respond to graceful, escalation, or force-kill termination.");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process proc, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return proc.HasExited;
        }
        catch (OperationCanceledException) { return proc.HasExited; }
        catch                              { return proc.HasExited; }
    }

    private static ProcessTerminationResult TermResult(
        OracleProcessInfo proc,
        TerminationStage stage,
        TimeSpan duration,
        string? error = null)
        => new()
        {
            ProcessId   = proc.ProcessId,
            ProcessName = proc.ProcessName,
            Kind        = proc.Kind,
            Stage       = stage,
            Duration    = duration,
            Error       = error,
        };

    private void RecordHistory(Guid sessionId, IReadOnlyList<ProcessTerminationResult> results)
    {
        lock (_historyLock)
        {
            if (!_terminationHistory.TryGetValue(sessionId, out var list))
            {
                list = new List<ProcessTerminationResult>();
                _terminationHistory[sessionId] = list;
            }
            list.AddRange(results);
        }
    }
}
