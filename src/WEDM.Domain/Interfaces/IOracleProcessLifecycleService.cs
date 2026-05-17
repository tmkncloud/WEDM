using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Governs the full lifecycle of Oracle middleware processes launched by WEDM.
///
/// Responsibilities:
///   • Record ownership when WEDM launches an Oracle tool process.
///   • Detect and classify all Oracle-related processes on the machine.
///   • Build parent-child process trees via PPID relationships.
///   • Execute staged shutdown (graceful → escalate → force-kill tree).
///   • Detect orphan processes from prior WEDM sessions or crashed deployments.
///   • Reconcile orphans: terminate WEDM-owned; report external ones for review.
///   • Verify cleanup: confirm all target processes have exited.
///   • Generate structured diagnostics for the deployment report.
///
/// Critical invariant:
///   WEDM must never automatically terminate a process classified as
///   <see cref="ProcessOwnership.External"/>. Such processes may belong to a
///   running customer Oracle environment. They are flagged for operator review only.
/// </summary>
public interface IOracleProcessLifecycleService
{
    // ── Launch registration ───────────────────────────────────────────────────

    /// <summary>
    /// Records that WEDM has launched a process with the given PID.
    /// Must be called immediately after the process is started, before any
    /// await or delay that would allow the OS to reuse the PID.
    ///
    /// The ownership record is persisted in the deployment session state so
    /// crash recovery can identify WEDM-owned orphans on next startup.
    /// </summary>
    /// <param name="context">Launch metadata: session, deployment, tool, Oracle home, temp root.</param>
    /// <param name="pid">The PID of the launched process.</param>
    ProcessOwnershipRecord RegisterLaunch(ProcessLaunchContext context, int pid);

    /// <summary>
    /// Adds a known child PID to an existing ownership record.
    /// Call when a launcher process spawns a child that WEDM tracks separately
    /// (e.g. OUI spawning a detached java subprocess).
    /// </summary>
    void TrackChildProcess(int parentPid, int childPid);

    // ── Process detection ─────────────────────────────────────────────────────

    /// <summary>
    /// Scans all running processes and returns those classified as Oracle-related.
    ///
    /// Classification is based on:
    ///   • Process name (java, javaw, nodemanager, ohs, wlsvc, …)
    ///   • Command line analysis (JVM args, classpath entries, -D properties)
    ///   • Working directory (contains Oracle or WebLogic path markers)
    ///   • Ownership records (is this PID registered with a WEDM session?)
    ///
    /// Processes that cannot be classified with confidence are returned as
    /// <see cref="OracleProcessKind.Unknown"/> and must not be killed automatically.
    /// </summary>
    IReadOnlyList<OracleProcessInfo> DetectOracleProcesses();

    /// <summary>
    /// Returns all processes currently registered as owned by the specified WEDM session.
    /// Includes both root processes and tracked child processes.
    /// Returns an empty list when the session has no registered processes.
    /// </summary>
    IReadOnlyList<ProcessOwnershipRecord> GetOwnedProcesses(Guid sessionId);

    // ── Process tree management ───────────────────────────────────────────────

    /// <summary>
    /// Builds the complete parent-child process tree rooted at <paramref name="rootPid"/>.
    ///
    /// Uses WMI <c>Win32_Process.ParentProcessId</c> relationships to enumerate
    /// all descendants recursively.  Returns null when the root process does not
    /// exist or has already exited.
    ///
    /// The returned tree may include non-Oracle children (e.g. conhost.exe spawned
    /// by a PowerShell host).  Each node carries its own <see cref="OracleProcessKind"/>
    /// so callers can filter before terminating.
    /// </summary>
    OracleProcessTree? BuildProcessTree(int rootPid);

    /// <summary>
    /// Builds process trees for all currently registered Oracle processes in the session.
    /// Returns one tree per root process registered with <see cref="RegisterLaunch"/>.
    /// </summary>
    IReadOnlyList<OracleProcessTree> BuildSessionProcessTrees(Guid sessionId);

    // ── Staged shutdown ───────────────────────────────────────────────────────

    /// <summary>
    /// Executes staged shutdown for the specified Oracle processes:
    ///
    ///   Stage 1 — Graceful: send WM_CLOSE / CloseMainWindow.
    ///   Stage 2 — Wait: pause for <see cref="ShutdownPolicy.GracefulTimeout"/>.
    ///   Stage 3 — Escalate: send SIGTERM / taskkill /IM.
    ///   Stage 4 — Force kill: <c>Process.Kill(entireProcessTree: true)</c>.
    ///
    /// External-owned processes are skipped with <see cref="TerminationStage.Skipped"/>
    /// when <see cref="ShutdownPolicy.SkipExternalProcesses"/> is true.
    ///
    /// Returns one <see cref="ProcessTerminationResult"/> per input process.
    /// </summary>
    Task<IReadOnlyList<ProcessTerminationResult>> ShutdownAsync(
        IEnumerable<OracleProcessInfo> processes,
        ShutdownPolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops all processes registered as owned by the specified session,
    /// then verifies they have exited.
    /// </summary>
    Task<ProcessLifecycleReport> CleanupSessionAsync(
        Guid sessionId,
        ShutdownPolicy? policy = null,
        CancellationToken cancellationToken = default);

    // ── Retry-safe pre-attempt cleanup ────────────────────────────────────────

    /// <summary>
    /// Cleans up all Oracle processes from the previous attempt before the next retry begins.
    ///
    /// Detects:
    ///   • Orphan OUI Java processes from the prior attempt.
    ///   • Stale WLST JVMs, OPatch JVMs, NodeManager remnants.
    ///   • Temp directory locks from prior-attempt JARs.
    ///
    /// Only terminates WEDM-owned processes from the prior attempt.
    /// External Oracle processes are reported but never terminated.
    /// </summary>
    Task<ProcessLifecycleReport> CleanupBeforeRetryAsync(
        Guid sessionId,
        int priorAttemptNumber,
        ShutdownPolicy? policy = null,
        CancellationToken cancellationToken = default);

    // ── Orphan detection and reconciliation ───────────────────────────────────

    /// <summary>
    /// Detects Oracle processes that appear to be orphaned from prior WEDM sessions
    /// or from crashed deployments.
    ///
    /// Classification:
    ///   • <see cref="ProcessOwnership.WedmPriorSession"/> — launched by a known prior WEDM session.
    ///   • <see cref="ProcessOwnership.Unknown"/> — unclassified Oracle JVM; may be orphan.
    ///   • <see cref="ProcessOwnership.External"/> — NOT a WEDM orphan; do not touch.
    /// </summary>
    IReadOnlyList<OracleProcessInfo> DetectOrphans(
        IEnumerable<Guid>? priorSessionIds = null);

    /// <summary>
    /// Reconciles detected orphans:
    ///   • Terminates WEDM-owned orphans (prior-session) according to <paramref name="options"/>.
    ///   • Logs external orphans for operator review without terminating them.
    ///   • Returns a report with recommendations for any orphan requiring manual action.
    /// </summary>
    Task<ProcessLifecycleReport> ReconcileOrphansAsync(
        OrphanReconciliationOptions options,
        CancellationToken cancellationToken = default);

    // ── Crash recovery ────────────────────────────────────────────────────────

    /// <summary>
    /// Scans for stale WEDM deployment sessions and associated orphan Oracle processes.
    /// Called at WEDM startup to identify and offer cleanup of crash remnants.
    ///
    /// Returns a report describing:
    ///   • Orphan processes found from prior deployment sessions.
    ///   • Which processes are safe to terminate automatically.
    ///   • Which processes require operator review before cleanup.
    ///   • Stale temp directories left by prior OUI extractions.
    /// </summary>
    Task<ProcessLifecycleReport> ScanForCrashRemnantsAsync(
        IEnumerable<ProcessOwnershipRecord> priorSessionRecords,
        CancellationToken cancellationToken = default);

    // ── Rollback integration ──────────────────────────────────────────────────

    /// <summary>
    /// Performs the process cleanup required before rollback can safely proceed:
    ///   1. Stop Oracle processes (NodeManager, WLST, OPatch, OUI remnants).
    ///   2. Stop Forms / OHS / AdminServer / ManagedServer.
    ///   3. Verify Oracle Home is no longer locked by any process.
    ///
    /// Returns a report; the rollback executor inspects <see cref="ProcessLifecycleReport.AllCleaned"/>
    /// to determine whether it is safe to delete Oracle Home directories.
    /// </summary>
    Task<ProcessLifecycleReport> PrepareForRollbackAsync(
        string oracleHome,
        Guid sessionId,
        ShutdownPolicy? policy = null,
        CancellationToken cancellationToken = default);

    // ── Verification ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that none of the specified PIDs are still running.
    /// Returns the list of processes that are still alive (should be empty on success).
    /// </summary>
    IReadOnlyList<OracleProcessInfo> VerifyCleanup(IEnumerable<int> pids);

    /// <summary>
    /// Returns true when no Oracle process is holding a file handle in the given Oracle Home directory.
    /// Used by rollback to confirm the home is safe to delete.
    /// </summary>
    bool IsOracleHomeLocked(string oracleHome);

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a full process lifecycle report for the specified session.
    /// Includes all detected, owned, terminated, and orphaned processes.
    /// </summary>
    ProcessLifecycleReport GenerateSessionReport(Guid sessionId);
}
