using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

/// <summary>Configuration for Remove WebLogic Environment / decommission workflows.</summary>
public sealed class DecommissionConfiguration
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Middleware Decommission";

    [JsonPropertyName("paths")]
    public DecommissionPathConfiguration Paths { get; set; } = new();

    [JsonPropertyName("options")]
    public DecommissionOptions Options { get; set; } = new();

    [JsonPropertyName("topology")]
    public EnvironmentTopology? DiscoveredTopology { get; set; }

    [JsonPropertyName("lastReport")]
    public DecommissionReport? LastReport { get; set; }
}

public sealed class DecommissionPathConfiguration
{
    public string OracleRoot       { get; set; } = @"D:\Oracle";
    public string MiddlewareHome   { get; set; } = @"D:\Oracle\Oracle_MW";
    public string DomainBase       { get; set; } = @"D:\Oracle\Oracle_MW\user_projects\domains";
    public string OracleInventory  { get; set; } = @"D:\Oracle\oraInventory";
    public string TempDirectory    { get; set; } = @"D:\Oracle\Temp";
    public string LogDirectory     { get; set; } = @"D:\Oracle\WEDM\logs";
    public string ReportsDirectory { get; set; } = @"D:\Oracle\WEDM\reports";
    public string SnapshotDirectory { get; set; } = @"D:\Oracle\WEDM\snapshots";
    public string? JavaHome        { get; set; }
}

public sealed class DecommissionOptions
{
    public bool DryRun { get; set; }
    public bool AggressiveCleanup { get; set; }
    public bool RemoveSnapshots { get; set; }
    public bool RemoveWindowsServices { get; set; } = true;
    public bool DetachInventoryHomes { get; set; } = true;
    public bool CleanupRegistry { get; set; } = true;
    public bool CleanupEnvironmentVariables { get; set; } = true;
    public bool DropRcuSchemas { get; set; }
    public string? RcuPrefix { get; set; }
    public string ConfirmationPhrase { get; set; } = "DECOMMISSION";
}

/// <summary>Discovered Oracle middleware estate topology.</summary>
public sealed class EnvironmentTopology
{
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;
    public string MachineName { get; init; } = Environment.MachineName;

    public List<OracleHomeDescriptor> MiddlewareHomes { get; init; } = [];
    public List<string> DomainHomes { get; init; } = [];
    public List<OracleWindowsServiceDescriptor> WindowsServices { get; init; } = [];
    public List<OracleProcessDescriptor> Processes { get; init; } = [];
    public List<int> ListeningPorts { get; init; } = [];
    public List<string> JdkInstallations { get; init; } = [];
    public List<string> TempExtractionFolders { get; init; } = [];
    public List<string> OrphanWarnings { get; init; } = [];
    public OracleInventorySnapshot? CentralInventory { get; set; }
    public List<OracleInventoryHomeRecord> InventoryHomes { get; init; } = [];
}

public sealed class OracleInventoryHomeRecord
{
    public string Path { get; init; } = string.Empty;
    public string? Name { get; init; }
    public bool PathExists { get; init; }
    public bool IsStale { get; init; }
    public bool IsRegisteredInCentralInventory { get; init; }
    public string? LocalInventoryPath { get; init; }
    public List<string> Issues { get; init; } = [];
}

public sealed class OracleWindowsServiceDescriptor
{
    public string ServiceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool IsOracleRelated { get; init; }
}

public sealed class OracleProcessDescriptor
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string? CommandLine { get; init; }
    public string Category { get; init; } = "Unknown";
}

public sealed class OracleInventoryAnalysis
{
    public string? InventoryRoot { get; set; }
    public OracleCentralInventoryState State { get; set; } = OracleCentralInventoryState.Healthy;
    public bool XmlValid { get; set; }
    public bool LockPresent { get; set; }
    public string? LockFilePath { get; set; }
    public List<OracleInventoryHomeRecord> Homes { get; } = [];
    public List<string> CorruptionWarnings { get; } = [];
}

public sealed class OracleHomeValidationResult
{
    public bool Passed { get; init; }
    public bool RebootRequired { get; init; }
    public List<string> Checks { get; init; } = [];
    public List<string> BlockingIssues { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class InventoryDetachResult
{
    public bool Success { get; init; }
    public bool DryRun { get; init; }
    public string OracleHome { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public List<string> Actions { get; init; } = [];
}

public sealed class OracleCleanupResult
{
    public bool Success { get; set; }
    public OracleCleanupMode Mode { get; init; }
    public List<string> RemovedPaths { get; init; } = [];
    public List<string> SkippedPaths { get; init; } = [];
    public List<string> ManualFollowUps { get; init; } = [];
}

public sealed class ProcessStopResult
{
    public int StoppedCount { get; init; }
    public int FailedCount { get; init; }
    public List<string> Messages { get; init; } = [];
}

public sealed class OracleConflictReport
{
    public bool HasBlockingConflicts { get; init; }
    public bool SuggestDecommission { get; init; }
    public bool ForceCleanInstallRecommended { get; init; }
    public List<OracleConflictFinding> Findings { get; init; } = [];
}

public sealed class OracleConflictFinding
{
    public string Code { get; init; } = string.Empty;
    public OracleConflictSeverity Severity { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Remediation { get; init; }
    public string? Path { get; init; }
}

public sealed class RetryIsolationContext
{
    public string IsolatedTempDirectory { get; init; } = string.Empty;
    public string? RegeneratedResponseFile { get; init; }
    public List<string> Actions { get; init; } = [];
}

public sealed class DecommissionReport
{
    public Guid ReportId { get; init; } = Guid.NewGuid();
    public DecommissionStatus FinalStatus { get; set; } = DecommissionStatus.NotStarted;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public TimeSpan? TotalDuration => CompletedAt.HasValue ? CompletedAt - StartedAt : null;

    public string ConfigurationName { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public EnvironmentTopology? Topology { get; set; }
    public List<DeploymentStep> Steps { get; set; } = [];
    public List<DecommissionAssetRecord> RemovedAssets { get; set; } = [];
    public List<DecommissionAssetRecord> FailedRemovals { get; set; } = [];
    public List<string> ManualRemediation { get; set; } = [];
    public List<string> OrphanWarnings { get; set; } = [];
    public OracleInventoryAnalysis? InventoryCleanup { get; set; }

    public int TotalSteps => Steps.Count;
    public int StepsSucceeded => Steps.Count(s => s.Status == StepStatus.Succeeded);
    public int StepsFailed => Steps.Count(s => s.Status == StepStatus.Failed);
}

public sealed class DecommissionAssetRecord
{
    public string AssetType { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Notes { get; init; }
}

/// <summary>Deploy-time Oracle lifecycle controls (conflict detection, force clean install, retry isolation).</summary>
public sealed class OracleLifecycleConfiguration
{
    [JsonPropertyName("forceCleanInstall")]
    public bool ForceCleanInstall { get; set; }

    [JsonPropertyName("suggestDecommissionOnConflict")]
    public bool SuggestDecommissionOnConflict { get; set; } = true;

    [JsonPropertyName("isolateRetries")]
    public bool IsolateRetries { get; set; } = true;

    // ── Rollback Safety ───────────────────────────────────────────────────────

    /// <summary>
    /// When true, Oracle rollback executors log every operation but skip all destructive actions
    /// (process termination, home directory deletion, inventory mutation, service removal).
    /// Safe to enable on production environments before committing to a real rollback.
    /// </summary>
    [JsonPropertyName("dryRunRollback")]
    public bool DryRunRollback { get; set; } = false;

    /// <summary>
    /// When true, Oracle/JVM processes that do not stop within <see cref="ProcessShutdownTimeoutSeconds"/>
    /// are force-killed (TerminateProcess / kill -9) during rollback.
    /// When false, rollback records them as residual warnings and continues.
    /// </summary>
    [JsonPropertyName("forceKillProcessesOnRollback")]
    public bool ForceKillProcessesOnRollback { get; set; } = true;

    /// <summary>
    /// Graceful shutdown window (seconds) given to each Oracle process before it is force-killed
    /// (only effective when <see cref="ForceKillProcessesOnRollback"/> is true).
    /// </summary>
    [JsonPropertyName("processShutdownTimeoutSeconds")]
    public int ProcessShutdownTimeoutSeconds { get; set; } = 30;
}
