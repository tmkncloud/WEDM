using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

// ═══════════════════════════════════════════════════════════════════════════════
// Environment Isolation Models
// ═══════════════════════════════════════════════════════════════════════════════
//
// These models power the WEDM environment isolation subsystem, which guarantees
// that every Oracle tool (OUI, WLST, OPatch, NodeManager, Forms, OHS) executes
// inside an explicitly controlled, sanitized environment — never inheriting
// ambient machine state from prior installs, retries, or other deployments.
//
// Lifecycle:
//   1. EnvironmentSnapshot captured at deployment start (pre-deployment)
//   2. DeploymentEnvironmentContext built from config + snapshot
//   3. IsolatedEnvironmentVariables derived per-tool from context
//   4. PowerShell preamble injected into every Oracle tool script
//   5. Post-deployment snapshot captured; drift detected
//   6. On rollback: environment variables restored via RollbackSnapshot
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Identifies which Oracle tool is being launched.
/// Used to select the appropriate environment profile.
/// </summary>
public enum OracleTool
{
    /// <summary>Oracle Universal Installer (java -jar installer.jar -silent)</summary>
    OUI,

    /// <summary>WebLogic Scripting Tool (wlst.cmd / wlst.sh)</summary>
    WLST,

    /// <summary>Oracle OPatch utility (opatch.bat napply / apply)</summary>
    OPatch,

    /// <summary>WebLogic NodeManager process</summary>
    NodeManager,

    /// <summary>Oracle Forms / Reports installer or runtime</summary>
    Forms,

    /// <summary>Oracle HTTP Server (OHS) component</summary>
    OHS,

    /// <summary>JDK installer (.msi / .exe)</summary>
    JdkInstaller,

    /// <summary>Repository Creation Utility (rcu.bat)</summary>
    RCU,

    /// <summary>Generic Oracle tool — apply base isolation only</summary>
    Generic
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Point-in-time snapshot of the machine environment variables relevant to Oracle middleware.
///
/// Captured at three lifecycle points:
///   • <see cref="SnapshotKind.PreDeployment"/>  — before any WEDM action runs
///   • <see cref="SnapshotKind.PostDeployment"/> — after all steps complete
///   • <see cref="SnapshotKind.PostRollback"/>   — after rollback completes
///
/// Snapshots are compared by <c>EnvironmentDriftDetector</c> to identify
/// unexpected mutations introduced by Oracle tooling or external processes.
/// </summary>
public sealed class EnvironmentSnapshot
{
    [JsonPropertyName("kind")]
    public SnapshotKind Kind { get; init; }

    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; init; }

    // ── Oracle / Middleware ───────────────────────────────────────────────────
    [JsonPropertyName("oracleHome")]
    public string? OracleHome { get; init; }

    [JsonPropertyName("wlHome")]
    public string? WlHome { get; init; }

    [JsonPropertyName("mwHome")]
    public string? MwHome { get; init; }

    [JsonPropertyName("wlstHome")]
    public string? WlstHome { get; init; }

    [JsonPropertyName("wlstProperties")]
    public string? WlstProperties { get; init; }

    // ── Java ──────────────────────────────────────────────────────────────────
    [JsonPropertyName("javaHome")]
    public string? JavaHome { get; init; }

    [JsonPropertyName("javaOpts")]
    public string? JavaOpts { get; init; }

    [JsonPropertyName("jvmArgs")]
    public string? JvmArgs { get; init; }

    [JsonPropertyName("classpath")]
    public string? Classpath { get; init; }

    // ── System ────────────────────────────────────────────────────────────────
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("temp")]
    public string? Temp { get; init; }

    [JsonPropertyName("tmp")]
    public string? Tmp { get; init; }

    [JsonPropertyName("userProfile")]
    public string? UserProfile { get; init; }

    [JsonPropertyName("appData")]
    public string? AppData { get; init; }

    [JsonPropertyName("programData")]
    public string? ProgramData { get; init; }

    [JsonPropertyName("systemRoot")]
    public string? SystemRoot { get; init; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }

    // ── OPatch residue ────────────────────────────────────────────────────────
    [JsonPropertyName("opatchDebug")]
    public string? OpatchDebug { get; init; }

    [JsonPropertyName("oracleSid")]
    public string? OracleSid { get; init; }

    [JsonPropertyName("tnsAdmin")]
    public string? TnsAdmin { get; init; }

    // ── PATH segments (parsed) ────────────────────────────────────────────────
    [JsonPropertyName("pathSegments")]
    public IReadOnlyList<string> PathSegments { get; init; } = [];

    /// <summary>
    /// Returns true when a PATH segment contains a known Oracle/JVM middleware pattern.
    /// Used for contamination detection.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> OraclePathSegments =>
        PathSegments.Where(s => IsOraclePathSegment(s)).ToList().AsReadOnly();

    public static bool IsOraclePathSegment(string segment)
    {
        var lower = segment.ToLowerInvariant();
        return lower.Contains("oracle") || lower.Contains("weblogic") || lower.Contains("wlserver")
            || lower.Contains("oracle_mw") || lower.Contains("middleware") || lower.Contains("opatch")
            || lower.Contains("jdk1.") || lower.Contains("jre1.") || lower.Contains("jdk-")
            || lower.Contains("wlst") || lower.Contains("mwhome") || lower.Contains("fmw");
    }
}

public enum SnapshotKind
{
    PreDeployment,
    PostDeployment,
    PostRollback,
    RetryAttempt,
    Manual
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-deployment-session runtime environment context.
/// Built once at session start and stored on <see cref="DeploymentConfiguration.EnvironmentContext"/>.
///
/// This is the authoritative source of truth for all environment variables
/// injected into Oracle tool subprocesses. Every tool that WEDM launches reads
/// its environment from here rather than from the ambient machine state.
///
/// Not serialised — rebuilt from config on each engine startup.
/// </summary>
public sealed class DeploymentEnvironmentContext
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public Guid   SessionId    { get; init; } = Guid.NewGuid();
    public Guid   DeploymentId { get; init; } = Guid.NewGuid();
    public int    AttemptNumber { get; init; } = 1;

    // ── Oracle paths ──────────────────────────────────────────────────────────
    /// <summary>Middleware home (WLS_HOME parent). Set after install; empty before.</summary>
    public string MiddlewareHome { get; set; } = string.Empty;

    /// <summary>Oracle Home — same as MiddlewareHome for WLS 12c+; may differ for 11g.</summary>
    public string OracleHome { get; set; } = string.Empty;

    /// <summary>OPatch directory under OracleHome.</summary>
    public string OpatchHome { get; set; } = string.Empty;

    /// <summary>Oracle Central Inventory path.</summary>
    public string InventoryLocation { get; init; } = string.Empty;

    // ── Java ──────────────────────────────────────────────────────────────────
    /// <summary>Validated JDK home used for all Java invocations in this session.</summary>
    public string JavaHome { get; set; } = string.Empty;

    /// <summary>Resolved java.exe path (JavaHome\bin\java.exe).</summary>
    public string JavaExe  => string.IsNullOrWhiteSpace(JavaHome)
        ? "java"
        : System.IO.Path.Combine(JavaHome, "bin", "java.exe");

    // ── Temp / Working dirs ───────────────────────────────────────────────────
    /// <summary>
    /// Session-scoped temp root. All WEDM-spawned processes use this as TEMP/TMP.
    /// Prevents cross-session contamination from OUI JAR extraction residue.
    /// Format: &lt;config.Paths.TempDirectory&gt;\wedm-session-&lt;SessionId[..8]&gt;\
    /// </summary>
    public string TempRoot { get; init; } = string.Empty;

    /// <summary>Working directory for OUI invocations (same as TempRoot).</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    // ── Sanitized PATH ────────────────────────────────────────────────────────
    /// <summary>
    /// The sanitized PATH string for this session.
    /// Contains only: JavaHome\bin + Windows system dirs.
    /// Oracle tool-specific paths are added per-tool by <see cref="IsolatedEnvironmentVariables"/>.
    /// </summary>
    public string SanitizedPath { get; set; } = string.Empty;

    // ── Pre-deployment snapshot ───────────────────────────────────────────────
    /// <summary>Machine environment captured before any deployment action ran.</summary>
    public EnvironmentSnapshot? PreDeploymentSnapshot { get; set; }

    // ── Isolation flags ───────────────────────────────────────────────────────
    /// <summary>When true, CLASSPATH is explicitly cleared for all Oracle tool invocations.</summary>
    public bool ClearClasspath { get; init; } = true;

    /// <summary>When true, stale ORACLE_HOME / WL_HOME / MW_HOME are explicitly unset.</summary>
    public bool ClearStaleOracleVars { get; init; } = true;

    /// <summary>When true, WLST_HOME / WLST_PROPERTIES are cleared before WLST runs.</summary>
    public bool ClearWlstResiduals { get; init; } = true;

    /// <summary>When true, JAVA_OPTS / _JAVA_OPTIONS / JVM_ARGS are cleared to prevent heap override.</summary>
    public bool ClearJvmOverrideVars { get; init; } = true;

    /// <summary>When true, OPATCH_DEBUG / related OPatch residuals are cleared.</summary>
    public bool ClearOpatchResiduals { get; init; } = true;

    // ── Snapshot metadata ─────────────────────────────────────────────────────
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Computed set of environment variables for a specific Oracle tool invocation.
/// Produced by <see cref="WEDM.Domain.Interfaces.IEnvironmentIsolationService.BuildIsolatedEnvironment"/>.
///
/// The <see cref="PowerShellPreamble"/> string is the key deliverable — it contains
/// all <c>$env:VAR = 'value'</c> and <c>Remove-Item Env:VAR</c> statements that
/// must be prepended to every PowerShell body that launches an Oracle tool.
/// </summary>
public sealed class IsolatedEnvironmentVariables
{
    /// <summary>The Oracle tool these variables are scoped to.</summary>
    public OracleTool Tool { get; init; }

    /// <summary>
    /// Variables to SET in the child process environment.
    /// Key = variable name, Value = value.
    /// </summary>
    public IReadOnlyDictionary<string, string> SetVariables { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Variable names to explicitly REMOVE (unset) in the child process environment.
    /// These are variables that must not be present to avoid contaminating the tool.
    /// </summary>
    public IReadOnlyList<string> ClearVariables { get; init; } = [];

    /// <summary>
    /// Ready-to-inject PowerShell block: Remove-Item Env: + $env:VAR = statements.
    /// Prepend this to any PowerShell script body before launching an Oracle tool.
    /// </summary>
    public string PowerShellPreamble { get; init; } = string.Empty;

    /// <summary>Summary description for logging / diagnostics.</summary>
    public string DiagnosticSummary { get; init; } = string.Empty;

    /// <summary>When built in this session.</summary>
    public DateTimeOffset BuiltAt { get; init; } = DateTimeOffset.UtcNow;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Report produced at the end of a deployment session summarising all environment
/// isolation activity: what was cleared, what was injected, any drift detected.
/// </summary>
public sealed class EnvironmentIsolationReport
{
    public Guid            SessionId    { get; init; }
    public DateTimeOffset  GeneratedAt  { get; init; } = DateTimeOffset.UtcNow;

    // ── Preamble statistics ───────────────────────────────────────────────────
    /// <summary>Number of Oracle tool invocations that had the isolation preamble applied.</summary>
    public int PreambleInjectionCount { get; set; }

    /// <summary>Tools that were launched with the isolation preamble.</summary>
    public List<string> IsolatedToolInvocations { get; init; } = [];

    // ── What was cleared ──────────────────────────────────────────────────────
    public List<string> ClearedVariables { get; init; } = [];
    public List<string> InjectedVariables { get; init; } = [];

    // ── Drift ─────────────────────────────────────────────────────────────────
    public EnvironmentDriftReport? DriftReport { get; set; }
    public bool                    DriftDetected => DriftReport?.HasDrift == true;

    // ── Validation ────────────────────────────────────────────────────────────
    public List<string> ValidationWarnings { get; init; } = [];
    public bool         IsClean            => !DriftDetected && ValidationWarnings.Count == 0;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Compares two <see cref="EnvironmentSnapshot"/> instances and reports unexpected mutations.
///
/// Populated by <c>EnvironmentDriftDetector</c>.
/// </summary>
public sealed class EnvironmentDriftReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public SnapshotKind   Baseline    { get; init; }
    public SnapshotKind   Current     { get; init; }

    /// <summary>True when any unexpected mutation was detected.</summary>
    public bool HasDrift => Findings.Count > 0;

    /// <summary>All detected mutations (both expected and unexpected).</summary>
    public List<EnvironmentDriftFinding> Findings { get; init; } = [];

    /// <summary>Only the unexpected mutations — the operator should review these.</summary>
    public IReadOnlyList<EnvironmentDriftFinding> UnexpectedFindings
        => Findings.Where(f => !f.IsExpected).ToList().AsReadOnly();

    /// <summary>Summary string for log output.</summary>
    public string Summary =>
        HasDrift
            ? $"{Findings.Count} mutation(s) detected ({UnexpectedFindings.Count} unexpected) between {Baseline}→{Current}"
            : $"No environment drift detected between {Baseline}→{Current}";
}

public sealed class EnvironmentDriftFinding
{
    public string VariableName  { get; init; } = string.Empty;
    public DriftKind Kind       { get; init; }
    public string? BaselineValue { get; init; }
    public string? CurrentValue  { get; init; }

    /// <summary>
    /// True when this mutation was expected (e.g. WEDM itself set JAVA_HOME post-install).
    /// False when it was introduced by an external process or Oracle installer residue.
    /// </summary>
    public bool IsExpected { get; init; }

    public string Description { get; init; } = string.Empty;
}

public enum DriftKind
{
    Added,       // variable did not exist before; now it does
    Removed,     // variable existed before; now it doesn't
    Changed,     // variable value changed
    PathAdded,   // new PATH segment appeared
    PathRemoved, // PATH segment disappeared
    PathReordered // PATH segment order changed
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Result of pre-launch environment validation for a given Oracle tool.</summary>
public sealed class EnvironmentValidationResult
{
    public OracleTool Tool          { get; init; }
    public bool       IsValid       { get; init; }
    public IReadOnlyList<string> Findings  { get; init; } = [];
    public IReadOnlyList<string> Warnings  { get; init; } = [];
    public IReadOnlyList<string> Blockers  { get; init; } = [];

    public bool HasBlockers => Blockers.Count > 0;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Result of PATH analysis: what's clean, what's stale, what's missing.
/// </summary>
public sealed class PathAnalysisResult
{
    public IReadOnlyList<string> AllSegments     { get; init; } = [];
    public IReadOnlyList<string> OracleSegments  { get; init; } = [];
    public IReadOnlyList<string> JavaSegments    { get; init; } = [];
    public IReadOnlyList<string> SystemSegments  { get; init; } = [];
    public IReadOnlyList<string> DuplicateSegments { get; init; } = [];
    public IReadOnlyList<string> StaleSegments   { get; init; } = [];
    public IReadOnlyList<string> MissingRequired { get; init; } = [];

    public bool HasStaleEntries   => StaleSegments.Count > 0;
    public bool HasDuplicates     => DuplicateSegments.Count > 0;
    public bool HasMissingRequired => MissingRequired.Count > 0;

    public string Summary =>
        $"PATH: {AllSegments.Count} segment(s) — " +
        $"{OracleSegments.Count} Oracle, {JavaSegments.Count} Java, " +
        $"{DuplicateSegments.Count} duplicate(s), {StaleSegments.Count} stale";
}
