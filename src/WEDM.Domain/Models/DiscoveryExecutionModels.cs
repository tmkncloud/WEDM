using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

/// <summary>Canonical result of a full migration environment discovery run.</summary>
public sealed class DiscoveryExecutionResult
{
    [JsonPropertyName("topology")]
    public MiddlewareTopologySnapshot Topology { get; set; } = new();

    [JsonPropertyName("formsMetadata")]
    public FormsReportsMetadataSnapshot FormsMetadata { get; set; } = new();

    [JsonPropertyName("oracleInventory")]
    public OracleInventorySnapshot OracleInventory { get; set; } = new();

    [JsonPropertyName("domainAnalysis")]
    public DomainAnalysisSnapshot DomainAnalysis { get; set; } = new();

    [JsonPropertyName("insights")]
    public List<EnvironmentDiscoveryFinding> Insights { get; set; } = [];

    [JsonPropertyName("stages")]
    public List<DiscoveryStageResult> Stages { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("usedRealScan")]
    public bool UsedRealScan { get; set; }

    [JsonPropertyName("totalDurationMs")]
    public long TotalDurationMs { get; set; }

    [JsonPropertyName("scanStatus")]
    public DiscoveryScanStatus ScanStatus { get; set; } = DiscoveryScanStatus.NotStarted;
}

public sealed class DiscoveryStageResult
{
    [JsonPropertyName("stage")]
    public DiscoveryStageKind Stage { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public DiscoveryStageStatus Status { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class OracleInventorySnapshot
{
    [JsonPropertyName("inventoryLoc")]
    public string? InventoryLoc { get; set; }

    [JsonPropertyName("oracleHomes")]
    public List<OracleHomeDescriptor> OracleHomes { get; set; } = [];

    [JsonPropertyName("installedProducts")]
    public List<InstalledProductDescriptor> InstalledProducts { get; set; } = [];

    [JsonPropertyName("patches")]
    public List<PatchInventoryRecord> Patches { get; set; } = [];

    [JsonPropertyName("opatchVersion")]
    public string? OpatchVersion { get; set; }

    [JsonPropertyName("inventoryHealthy")]
    public bool InventoryHealthy { get; set; } = true;

    [JsonPropertyName("inventoryWarning")]
    public string? InventoryWarning { get; set; }

    [JsonIgnore]
    public int HomeCount => OracleHomes.Count;
}

public sealed class OracleHomeDescriptor
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class InstalledProductDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("oracleHome")]
    public string? OracleHome { get; set; }
}

public sealed class PatchInventoryRecord
{
    [JsonPropertyName("patchId")]
    public string PatchId { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("appliedOn")]
    public string? AppliedOn { get; set; }
}

public sealed class DomainAnalysisSnapshot
{
    [JsonPropertyName("domainHome")]
    public string? DomainHome { get; set; }

    [JsonPropertyName("domainName")]
    public string? DomainName { get; set; }

    [JsonPropertyName("adminServerName")]
    public string? AdminServerName { get; set; }

    [JsonPropertyName("adminListenPort")]
    public int? AdminListenPort { get; set; }

    [JsonPropertyName("productionMode")]
    public bool? ProductionMode { get; set; }

    [JsonPropertyName("bootPropertiesPresent")]
    public bool BootPropertiesPresent { get; set; }

    [JsonPropertyName("jdbcResourceCount")]
    public int JdbcResourceCount { get; set; }

    [JsonPropertyName("deploymentTargetCount")]
    public int DeploymentTargetCount { get; set; }

    [JsonPropertyName("machineCount")]
    public int MachineCount { get; set; }

    [JsonPropertyName("nodeManagerPropertiesPath")]
    public string? NodeManagerPropertiesPath { get; set; }

    [JsonPropertyName("nodeManagerSecure")]
    public bool? NodeManagerSecure { get; set; }

    [JsonPropertyName("startupScriptPaths")]
    public List<string> StartupScriptPaths { get; set; } = [];

    [JsonPropertyName("deprecatedJvmFlags")]
    public List<string> DeprecatedJvmFlags { get; set; } = [];

    [JsonPropertyName("parseWarnings")]
    public List<string> ParseWarnings { get; set; } = [];

    [JsonPropertyName("parseHealthy")]
    public bool ParseHealthy { get; set; } = true;
}

public sealed class DiscoveryScanOptions
{
    [JsonPropertyName("middlewareHome")]
    public string? MiddlewareHome { get; set; }

    [JsonPropertyName("domainHome")]
    public string? DomainHome { get; set; }

    [JsonPropertyName("formsHome")]
    public string? FormsHome { get; set; }

    [JsonPropertyName("reportsHome")]
    public string? ReportsHome { get; set; }

    [JsonPropertyName("inventoryLoc")]
    public string? InventoryLoc { get; set; }

    [JsonPropertyName("scanTimeoutSeconds")]
    public int ScanTimeoutSeconds { get; set; } = 120;

    [JsonPropertyName("allowSimulatedFallback")]
    public bool AllowSimulatedFallback { get; set; }

    /// <summary>When true (demo only), runs simulated discovery without requiring valid paths.</summary>
    [JsonPropertyName("forceSimulation")]
    public bool ForceSimulation { get; set; }
}
