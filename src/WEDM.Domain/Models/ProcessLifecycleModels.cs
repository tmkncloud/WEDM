using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

// ═══════════════════════════════════════════════════════════════════════════════
// Oracle Process Lifecycle Models
// ═══════════════════════════════════════════════════════════════════════════════
//
// These models power WEDM's Oracle-aware process lifecycle management subsystem.
// Every Oracle tool launched by WEDM is tracked from spawn to termination.
// Orphan detection and external-process protection are first-class concerns.
//
// Classification hierarchy:
//   OracleProcessKind  — what kind of Oracle process is this?
//   ProcessOwnership   — who owns it (WEDM or external)?
//   TerminationStage   — how was it stopped (or did it fail)?
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Classification of an Oracle-related process by its functional role.
/// Used to determine the correct shutdown sequence and to protect customer-owned processes.
/// </summary>
public enum OracleProcessKind
{
    /// <summary>Oracle Universal Installer java subprocess (-jar fmw_*.jar -silent).</summary>
    OUI,

    /// <summary>WebLogic Scripting Tool JVM (wlst.cmd / wlst.sh).</summary>
    WLST,

    /// <summary>Oracle OPatch JVM (opatch.bat / opatch napply).</summary>
    OPatch,

    /// <summary>WebLogic NodeManager daemon process.</summary>
    NodeManager,

    /// <summary>WebLogic Administration Server (weblogic.Server -name AdminServer).</summary>
    AdminServer,

    /// <summary>WebLogic Managed Server (weblogic.Server -name [managed-name]).</summary>
    ManagedServer,

    /// <summary>Oracle Forms runtime JVM or installer.</summary>
    FormsRuntime,

    /// <summary>Oracle Reports runtime process.</summary>
    ReportsRuntime,

    /// <summary>Oracle HTTP Server (httpd / ohs) process.</summary>
    OHS,

    /// <summary>JDK installer (java.exe / msiexec) launched by WEDM.</summary>
    JdkInstaller,

    /// <summary>Repository Creation Utility (rcu.bat) JVM.</summary>
    RCU,

    /// <summary>
    /// Unclassified Java/JVM process with Oracle middleware markers in the command line.
    /// May be an orphaned subprocess; treat cautiously.
    /// </summary>
    OrphanJvm,

    /// <summary>
    /// Cannot be classified with confidence.
    /// MUST NOT be terminated automatically — requires operator review.
    /// </summary>
    Unknown
}

/// <summary>Process ownership classification: who launched this process?</summary>
public enum ProcessOwnership
{
    /// <summary>Launched by this WEDM session — safe to manage.</summary>
    WedmOwned,

    /// <summary>Appears to have been launched by a prior WEDM session — orphan candidate.</summary>
    WedmPriorSession,

    /// <summary>
    /// Launched by an external actor (customer deployment, another product).
    /// WEDM must NOT terminate external processes automatically.
    /// </summary>
    External,

    /// <summary>Ownership cannot be determined from available evidence.</summary>
    Unknown
}

/// <summary>The stage at which a process was terminated during staged shutdown.</summary>
public enum TerminationStage
{
    /// <summary>Process had already exited before termination was attempted.</summary>
    AlreadyExited,

    /// <summary>Process stopped cleanly in response to CloseMainWindow / graceful signal.</summary>
    Graceful,

    /// <summary>Process stopped after the graceful timeout expired (escalated kill).</summary>
    Escalated,

    /// <summary>Process was force-killed including its entire process tree.</summary>
    ForcedKill,

    /// <summary>Termination failed — process is still running.</summary>
    Failed,

    /// <summary>Process was skipped because it is externally owned.</summary>
    Skipped
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Rich information about a single detected Oracle-related process.
/// Extends the basic <see cref="OracleProcessDescriptor"/> with ownership, tree, and classification data.
/// </summary>
public sealed record OracleProcessInfo
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public int    ProcessId       { get; init; }
    public int    ParentProcessId { get; init; }
    public string ProcessName     { get; init; } = string.Empty;

    // ── Classification ────────────────────────────────────────────────────────
    public OracleProcessKind Kind      { get; init; } = OracleProcessKind.Unknown;
    public ProcessOwnership  Ownership { get; init; } = ProcessOwnership.Unknown;

    // ── Command line evidence ─────────────────────────────────────────────────
    public string? CommandLine      { get; init; }
    public string? WorkingDirectory { get; init; }

    /// <summary>Key JVM arguments detected (e.g. -Dweblogic.Name, -Djava.class.path fragment).</summary>
    public IReadOnlyList<string> DetectedJvmArgs { get; init; } = [];

    /// <summary>Oracle middleware home references found in command line or JVM args.</summary>
    public IReadOnlyList<string> OracleHomeRefs  { get; init; } = [];

    // ── Ownership evidence ────────────────────────────────────────────────────
    /// <summary>Session ID of the WEDM session that owns this process, if known.</summary>
    public Guid? OwnerSessionId { get; init; }

    /// <summary>Deployment attempt number of the WEDM session that owns this process, if known.</summary>
    public int? OwnerAttemptNumber { get; init; }

    // ── Runtime metadata ──────────────────────────────────────────────────────
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>How long this process has been running as of detection time.</summary>
    public TimeSpan? Runtime => StartTime.HasValue ? DateTimeOffset.UtcNow - StartTime.Value : null;

    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;

    // ── Locking ───────────────────────────────────────────────────────────────
    /// <summary>True when this process holds a file lock on a known Oracle inventory path.</summary>
    public bool HoldsInventoryLock { get; init; }

    /// <summary>Paths this process appears to have locked (file handles in Oracle directories).</summary>
    public IReadOnlyList<string> LockedPaths { get; init; } = [];

    // ── Diagnostic summary ────────────────────────────────────────────────────
    public string ClassificationReason { get; init; } = string.Empty;

    public override string ToString()
        => $"{ProcessName}(PID={ProcessId}, PPID={ParentProcessId}, Kind={Kind}, Ownership={Ownership})";
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Parent-child process tree rooted at a single Oracle process.
/// Built by <c>ProcessTreeBuilder</c> via WMI PPID relationships.
/// </summary>
public sealed class OracleProcessTree
{
    /// <summary>The root process of this tree.</summary>
    public OracleProcessInfo Root { get; init; } = null!;

    /// <summary>Direct children of the root.</summary>
    public IReadOnlyList<OracleProcessTree> Children { get; init; } = [];

    /// <summary>Flat enumeration of all processes in this tree (root + all descendants).</summary>
    public IEnumerable<OracleProcessInfo> All()
    {
        yield return Root;
        foreach (var child in Children)
            foreach (var p in child.All())
                yield return p;
    }

    /// <summary>Total number of processes in this tree.</summary>
    public int TotalCount => All().Count();

    /// <summary>True when any process in the tree holds an Oracle inventory lock.</summary>
    public bool HasInventoryLock => All().Any(p => p.HoldsInventoryLock);

    public override string ToString()
        => $"Tree({Root.ProcessName} PID={Root.ProcessId}, {TotalCount} process(es))";
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Records metadata about a process launched by WEDM.
/// Persisted in the deployment session state so that crash recovery can
/// identify WEDM-owned orphan processes on next startup.
/// </summary>
public sealed class ProcessOwnershipRecord
{
    // ── Session identity ──────────────────────────────────────────────────────
    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("deploymentId")]
    public Guid DeploymentId { get; init; }

    [JsonPropertyName("attemptNumber")]
    public int AttemptNumber { get; init; } = 1;

    // ── Process identity ──────────────────────────────────────────────────────
    [JsonPropertyName("rootPid")]
    public int RootProcessId { get; init; }

    [JsonPropertyName("childPids")]
    public List<int> ChildProcessIds { get; init; } = [];

    [JsonPropertyName("tool")]
    public OracleProcessKind Tool { get; init; }

    // ── Launch context ────────────────────────────────────────────────────────
    [JsonPropertyName("launchTime")]
    public DateTimeOffset LaunchTime { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("oracleHome")]
    public string OracleHome { get; init; } = string.Empty;

    [JsonPropertyName("tempDirectory")]
    public string TempDirectory { get; init; } = string.Empty;

    [JsonPropertyName("workingDirectory")]
    public string WorkingDirectory { get; init; } = string.Empty;

    [JsonPropertyName("commandLine")]
    public string? CommandLine { get; init; }

    // ── Lifecycle state ───────────────────────────────────────────────────────
    [JsonPropertyName("terminatedAt")]
    public DateTimeOffset? TerminatedAt { get; set; }

    [JsonPropertyName("terminationStage")]
    public TerminationStage? TerminationStage { get; set; }

    [JsonIgnore]
    public bool IsTerminated => TerminatedAt.HasValue;

    [JsonIgnore]
    public TimeSpan? ActiveDuration => TerminatedAt.HasValue
        ? TerminatedAt.Value - LaunchTime : null;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Context provided to the lifecycle service when WEDM is about to launch an Oracle tool.
/// Used to record the launch and associate subsequent child processes with this session.
/// </summary>
public sealed class ProcessLaunchContext
{
    public Guid              SessionId    { get; init; }
    public Guid              DeploymentId { get; init; }
    public int               AttemptNumber { get; init; } = 1;
    public OracleProcessKind Tool          { get; init; }
    public string            OracleHome    { get; init; } = string.Empty;
    public string            TempRoot      { get; init; } = string.Empty;
    public string            WorkingDirectory { get; init; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Result of terminating a single process during staged shutdown.</summary>
public sealed class ProcessTerminationResult
{
    public int              ProcessId   { get; init; }
    public string           ProcessName { get; init; } = string.Empty;
    public OracleProcessKind Kind       { get; init; }
    public TerminationStage Stage       { get; init; }
    public TimeSpan         Duration    { get; init; }
    public string?          Error       { get; init; }

    public bool Succeeded => Stage is TerminationStage.AlreadyExited
        or TerminationStage.Graceful
        or TerminationStage.Escalated
        or TerminationStage.ForcedKill
        or TerminationStage.Skipped;

    public override string ToString()
        => $"{ProcessName}(PID={ProcessId}) → {Stage} in {Duration.TotalSeconds:F1}s";
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Shutdown policy for a process lifecycle operation.
/// Controls timeout durations and force-kill behaviour.
/// </summary>
public sealed class ShutdownPolicy
{
    /// <summary>Time to wait after sending WM_CLOSE / CloseMainWindow before escalating.</summary>
    public TimeSpan GracefulTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time to wait after escalation before force-killing the process tree.</summary>
    public TimeSpan EscalationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>When true, force-kill the entire process tree if escalation also fails.</summary>
    public bool ForceKillOnEscalationFailure { get; init; } = true;

    /// <summary>When true, skip external-owned processes instead of returning an error.</summary>
    public bool SkipExternalProcesses { get; init; } = true;

    /// <summary>When true, log all actions but perform no actual process operations (dry-run).</summary>
    public bool DryRun { get; init; } = false;

    public static ShutdownPolicy Default => new();

    public static ShutdownPolicy Aggressive => new()
    {
        GracefulTimeout   = TimeSpan.FromSeconds(10),
        EscalationTimeout = TimeSpan.FromSeconds(5),
        ForceKillOnEscalationFailure = true,
    };

    public static ShutdownPolicy Conservative => new()
    {
        GracefulTimeout   = TimeSpan.FromMinutes(2),
        EscalationTimeout = TimeSpan.FromSeconds(30),
        ForceKillOnEscalationFailure = false,
    };
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Options for orphan reconciliation (safe cleanup of prior-session Oracle processes).
/// </summary>
public sealed class OrphanReconciliationOptions
{
    /// <summary>Session IDs to consider as "prior WEDM sessions" for orphan detection.</summary>
    public IReadOnlyList<Guid> PriorSessionIds { get; init; } = [];

    /// <summary>When true, also consider any unknown Oracle JVM as an orphan candidate.</summary>
    public bool IncludeUnknownJvms { get; init; } = false;

    /// <summary>When true, terminate WEDM-owned orphans automatically.</summary>
    public bool AutoTerminateWedmOrphans { get; init; } = true;

    /// <summary>
    /// When true, log suggestions for external orphans but do NOT terminate them.
    /// External Oracle processes are never killed automatically regardless of this setting.
    /// </summary>
    public bool SuggestExternalCleanup { get; init; } = true;

    public ShutdownPolicy Policy { get; init; } = ShutdownPolicy.Default;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Structured report produced by a process lifecycle operation.
/// Covers detection, termination, orphan reconciliation, and verification.
/// </summary>
public sealed class ProcessLifecycleReport
{
    public Guid             SessionId    { get; init; }
    public DateTimeOffset   GeneratedAt  { get; init; } = DateTimeOffset.UtcNow;

    // ── Detection ─────────────────────────────────────────────────────────────
    public IReadOnlyList<OracleProcessInfo>       DetectedProcesses   { get; init; } = [];
    public IReadOnlyList<OracleProcessTree>       DetectedTrees       { get; init; } = [];

    // ── Termination ───────────────────────────────────────────────────────────
    public IReadOnlyList<ProcessTerminationResult> TerminationResults  { get; init; } = [];

    public int TotalTerminated  => TerminationResults.Count(r => r.Succeeded);
    public int TotalFailed      => TerminationResults.Count(r => !r.Succeeded);
    public int TotalSkipped     => TerminationResults.Count(r => r.Stage == TerminationStage.Skipped);

    // ── Orphan reconciliation ─────────────────────────────────────────────────
    public IReadOnlyList<OracleProcessInfo>       OrphansFound        { get; init; } = [];
    public IReadOnlyList<OracleProcessInfo>       OrphansReconciled   { get; init; } = [];
    public IReadOnlyList<OracleProcessInfo>       ExternalOrphans     { get; init; } = [];
    public IReadOnlyList<string>                  ManualActionItems   { get; init; } = [];

    // ── Verification ─────────────────────────────────────────────────────────
    public bool AllCleaned { get; init; }
    public IReadOnlyList<OracleProcessInfo> StillRunning { get; init; } = [];

    // ── Summary ───────────────────────────────────────────────────────────────
    public string Summary =>
        $"Detected={DetectedProcesses.Count}, Terminated={TotalTerminated}, " +
        $"Failed={TotalFailed}, Orphans={OrphansFound.Count}, Clean={AllCleaned}";
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Classification signal used internally by <c>OracleProcessClassifier</c>.
/// Tracks how confident the classifier is and which evidence led to the classification.
/// </summary>
public sealed class ProcessClassificationResult
{
    public OracleProcessKind Kind       { get; init; } = OracleProcessKind.Unknown;
    public int               Confidence { get; init; }  // 0–100
    public string            Reason     { get; init; } = string.Empty;

    /// <summary>Oracle middleware home paths extracted from the command line.</summary>
    public IReadOnlyList<string> ExtractedOracleHomes { get; init; } = [];

    /// <summary>Relevant JVM arguments extracted from the command line.</summary>
    public IReadOnlyList<string> ExtractedJvmArgs     { get; init; } = [];
}
