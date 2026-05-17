namespace WEDM.Domain.Models;

/// <summary>
/// Per-step compensation record populated by each Oracle installer step immediately after it succeeds.
/// Stored on <see cref="DeploymentStep.RollbackCompensation"/> so that Oracle rollback executors
/// can precisely reverse the state this specific step created, rather than relying solely on
/// <see cref="DeploymentConfiguration"/> paths (which may have changed between steps).
///
/// Lifecycle:
///   • Created by OUI-launching steps (InstallWebLogic, InstallFormsReports, InstallOHSWebTier, ConfigureJavaHome)
///     before returning <see cref="StepExecutionResult.Ok"/>.
///   • Read by the corresponding Oracle rollback executor during rollback.
///   • Null when the step has not yet succeeded or isolation is disabled.
///   • Never serialised — rebuilt fresh on each engine execution.
/// </summary>
public sealed class OracleRollbackCompensation
{
    // ── Oracle Homes ──────────────────────────────────────────────────────────

    /// <summary>
    /// Oracle Home paths created by this step.
    /// For InstallWebLogic: [config.Paths.MiddlewareHome].
    /// For InstallOHSWebTier: [OHS_Home path].
    /// For InstallFormsReports: [Forms/Reports home path].
    /// </summary>
    public List<string> OracleHomePaths { get; set; } = [];

    /// <summary>Oracle Central Inventory path used during this install.</summary>
    public string? OracleInventoryPath { get; set; }

    /// <summary>
    /// Oracle Central Inventory state captured immediately before OUI launched.
    /// Enables before/after diffs in the rollback report and confirms which homes
    /// were registered by this specific step.
    /// </summary>
    public OracleInventorySnapshot? InventorySnapshotBefore { get; set; }

    // ── Windows Services ──────────────────────────────────────────────────────

    /// <summary>
    /// Names of Windows services registered by or for this step (as they appear in SCM).
    /// For example: "WLS_AdminServer", "WLS NodeManager", "OracleOHSComponent1".
    /// Rollback executors stop and remove these before deleting the home directory.
    /// </summary>
    public List<string> CreatedServiceNames { get; set; } = [];

    // ── Environment Variables ─────────────────────────────────────────────────

    /// <summary>
    /// Machine-level environment variable names set by this step.
    /// For ConfigureJavaHome: ["JAVA_HOME"]. For ConfigureRegistry: ["ORACLE_HOME"].
    /// Rollback executors remove these (matching the configured value to avoid false removals).
    /// </summary>
    public List<string> SetEnvironmentVariableNames { get; set; } = [];

    // ── Registry ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Full HKLM paths of registry keys created by this step.
    /// For example: @"SOFTWARE\ORACLE\KEY_WEDM_OracleMW".
    /// </summary>
    public List<string> CreatedRegistryKeyPaths { get; set; } = [];

    // ── Generated Files ───────────────────────────────────────────────────────

    /// <summary>
    /// Response files, silent XML files, inventory pointer files, and other
    /// WEDM-generated artefacts written by this step.
    /// Cleaned up during rollback after the home directory is removed.
    /// </summary>
    public List<string> GeneratedFilePaths { get; set; } = [];

    // ── Patches ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Oracle patch IDs (PSU/RU) applied by this step.
    /// When non-empty, rollback guidance includes OPatch-rollback steps.
    /// </summary>
    public List<string> AppliedPatchIds { get; set; } = [];

    // ── Metadata ──────────────────────────────────────────────────────────────

    /// <summary>UTC timestamp when this record was captured (immediately after step success).</summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Outcome of Oracle environment verification run after rollback completes.
/// Produced by <see cref="OracleRollbackVerificationResult"/> (engine) and attached to the
/// deployment report for operator review.
/// </summary>
public sealed class OracleRollbackVerificationResult
{
    /// <summary>
    /// True when all targeted Oracle state has been confirmed reversed:
    ///   • Target home is no longer registered in the Central Inventory
    ///   • No Oracle inventory lock files remain
    ///   • No orphan OUI / java-OUI processes detected
    ///   • Target home directory does not exist
    /// </summary>
    public bool IsClean { get; init; }

    /// <summary>
    /// All verification findings — both pass (✔) and fail (✗).
    /// Always populated for audit completeness even when IsClean = true.
    /// </summary>
    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>
    /// Non-fatal observations that do not prevent future installs but
    /// the operator should be aware of (e.g. stale temp files, partial lock files).
    /// </summary>
    public IReadOnlyList<string> RemainingWarnings { get; init; } = [];

    /// <summary>
    /// Actions the operator must complete manually before the environment is
    /// considered fully clean and ready for a fresh install attempt.
    /// </summary>
    public IReadOnlyList<string> ManualActionsRequired { get; init; } = [];
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Oracle-specific rollback narrative surfaced in the deployment report.
/// Aggregated from all Oracle rollback executor runs during a single rollback pass.
/// Attached to <see cref="RollbackSummary.OracleDetails"/> for HTML report rendering.
///
/// Accumulation pattern:
///   Each Oracle rollback executor writes to <see cref="DeploymentConfiguration.OracleRollback"/>
///   (initialising it on first write). The workflow engine copies it into the report after rollback.
/// </summary>
public sealed class OracleRollbackReport
{
    // ── Safety ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, rollback was run in dry-run mode. All operations were logged
    /// but no destructive actions (process termination, filesystem deletion,
    /// inventory mutation) were performed.
    /// </summary>
    public bool DryRunMode { get; set; } = false;

    // ── What was removed ──────────────────────────────────────────────────────

    /// <summary>Oracle Home directories that were deleted during rollback.</summary>
    public List<string> RemovedHomes { get; set; } = [];

    /// <summary>Central Inventory entries that were detached (format: "NAME @ PATH").</summary>
    public List<string> DetachedInventoryEntries { get; set; } = [];

    /// <summary>Windows services that were stopped and removed from SCM.</summary>
    public List<string> StoppedAndRemovedServices { get; set; } = [];

    /// <summary>Machine-level environment variables that were removed.</summary>
    public List<string> RemovedEnvironmentVariables { get; set; } = [];

    /// <summary>Oracle / JVM processes that were stopped (format: "PID processName").</summary>
    public List<string> StoppedProcesses { get; set; } = [];

    /// <summary>WEDM-generated files (response files, silent XML, etc.) that were deleted.</summary>
    public List<string> RemovedGeneratedFiles { get; set; } = [];

    // ── Post-rollback verification ────────────────────────────────────────────

    /// <summary>True when the Central Inventory is confirmed clean post-rollback.</summary>
    public bool InventoryClean { get; set; }

    /// <summary>True when no Oracle inventory lock files remain in the inventory directory.</summary>
    public bool NoOuiLocks { get; set; }

    /// <summary>True when no orphan Oracle/JVM middleware processes remain running.</summary>
    public bool NoOrphanProcesses { get; set; }

    // ── Residual ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Non-blocking warnings (files locked by the OS, stale temp directories
    /// the executor couldn't delete, etc.).
    /// </summary>
    public List<string> RemainingWarnings { get; set; } = [];

    /// <summary>
    /// Actions the operator must complete manually before the environment is
    /// ready for a fresh install attempt.
    /// Examples: RCU schema drop, OPatch rollback, VC++ uninstall.
    /// </summary>
    public List<string> ManualActionsRequired { get; set; } = [];

    /// <summary>When this report section was generated.</summary>
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>True when the environment is considered fully clean for a retry.</summary>
    public bool IsFullyClean
        => InventoryClean && NoOuiLocks && NoOrphanProcesses
           && RemainingWarnings.Count == 0
           && ManualActionsRequired.Count == 0;

    /// <summary>Merges another report's lists into this one (used when multiple executors write to the same report).</summary>
    public void MergeFrom(OracleRollbackReport other)
    {
        RemovedHomes                .AddRange(other.RemovedHomes);
        DetachedInventoryEntries    .AddRange(other.DetachedInventoryEntries);
        StoppedAndRemovedServices   .AddRange(other.StoppedAndRemovedServices);
        RemovedEnvironmentVariables .AddRange(other.RemovedEnvironmentVariables);
        StoppedProcesses            .AddRange(other.StoppedProcesses);
        RemovedGeneratedFiles       .AddRange(other.RemovedGeneratedFiles);
        RemainingWarnings           .AddRange(other.RemainingWarnings);
        ManualActionsRequired       .AddRange(other.ManualActionsRequired);

        // Verification flags: take the most pessimistic (AND logic)
        InventoryClean    = InventoryClean    && other.InventoryClean;
        NoOuiLocks        = NoOuiLocks        && other.NoOuiLocks;
        NoOrphanProcesses = NoOrphanProcesses && other.NoOrphanProcesses;
    }
}
