using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

/// <summary>
/// Dedicated session model for upgrade / migrate workflows.
/// Kept separate from <see cref="DeploymentConfiguration"/> to avoid mixing deployment and migration concerns.
/// </summary>
public sealed class MigrationConfiguration
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Middleware Migration Project";

    [JsonPropertyName("operationMode")]
    public WedmOperationMode OperationMode { get; set; } = WedmOperationMode.None;

    [JsonPropertyName("source")]
    public MigrationEnvironmentProfile Source { get; set; } = new();

    [JsonPropertyName("target")]
    public MigrationEnvironmentProfile Target { get; set; } = new();

    [JsonPropertyName("strategy")]
    public MigrationStrategyKind Strategy { get; set; } = MigrationStrategyKind.PhasedModuleMigration;

    [JsonPropertyName("topology")]
    public MiddlewareTopologySnapshot Topology { get; set; } = new();

    [JsonPropertyName("formsMetadata")]
    public FormsReportsMetadataSnapshot FormsMetadata { get; set; } = new();

    [JsonPropertyName("readiness")]
    public MigrationReadinessSnapshot Readiness { get; set; } = new();

    [JsonPropertyName("compatibilityFindings")]
    public List<CompatibilityFinding> CompatibilityFindings { get; set; } = [];

    [JsonPropertyName("validationMessages")]
    public List<MigrationValidationMessage> ValidationMessages { get; set; } = [];

    [JsonPropertyName("discoveryCompleted")]
    public bool DiscoveryCompleted { get; set; }

    [JsonPropertyName("assessmentCompleted")]
    public bool AssessmentCompleted { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("discoveryInsights")]
    public List<EnvironmentDiscoveryFinding> DiscoveryInsights { get; set; } = [];

    [JsonPropertyName("discoveryDurationMs")]
    public long DiscoveryDurationMs { get; set; }

    [JsonPropertyName("assessmentDurationMs")]
    public long AssessmentDurationMs { get; set; }

    [JsonPropertyName("lastSavedUtc")]
    public DateTimeOffset? LastSavedUtc { get; set; }

    [JsonPropertyName("oracleInventory")]
    public OracleInventorySnapshot OracleInventory { get; set; } = new();

    [JsonPropertyName("domainAnalysis")]
    public DomainAnalysisSnapshot DomainAnalysis { get; set; } = new();

    [JsonPropertyName("discoveryStages")]
    public List<DiscoveryStageResult> DiscoveryStages { get; set; } = [];

    [JsonPropertyName("discoveryWarnings")]
    public List<string> DiscoveryWarnings { get; set; } = [];

    [JsonPropertyName("discoveryUsedRealScan")]
    public bool DiscoveryUsedRealScan { get; set; }

    [JsonPropertyName("transformationCompleted")]
    public bool TransformationCompleted { get; set; }

    [JsonPropertyName("transformationDurationMs")]
    public long TransformationDurationMs { get; set; }

    [JsonPropertyName("transformationWorkspacePath")]
    public string? TransformationWorkspacePath { get; set; }

    [JsonPropertyName("transformation")]
    public TransformationExecutionResult? Transformation { get; set; }

    [JsonPropertyName("formsModernization")]
    public FormsModernizationSnapshot FormsModernization { get; set; } = new();

    [JsonPropertyName("reportsModernization")]
    public ReportsModernizationSnapshot ReportsModernization { get; set; } = new();

    [JsonPropertyName("executionCompleted")]
    public bool ExecutionCompleted { get; set; }

    [JsonPropertyName("executionDurationMs")]
    public long ExecutionDurationMs { get; set; }

    [JsonPropertyName("execution")]
    public MigrationExecutionResult? Execution { get; set; }
}

public sealed class MigrationEnvironmentProfile
{
    [JsonPropertyName("release")]
    public MiddlewareReleaseKind Release { get; set; } = MiddlewareReleaseKind.Unknown;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("middlewareHome")]
    public string? MiddlewareHome { get; set; }

    [JsonPropertyName("domainHome")]
    public string? DomainHome { get; set; }

    [JsonPropertyName("formsHome")]
    public string? FormsHome { get; set; }

    [JsonPropertyName("reportsHome")]
    public string? ReportsHome { get; set; }

    [JsonPropertyName("javaHome")]
    public string? JavaHome { get; set; }

    [JsonPropertyName("hostName")]
    public string? HostName { get; set; }
}

public sealed class MiddlewareTopologySnapshot
{
    [JsonPropertyName("adminUrl")]
    public string? AdminServerUrl { get; set; }

    [JsonPropertyName("domainName")]
    public string? DomainName { get; set; }

    [JsonPropertyName("managedServerCount")]
    public int ManagedServerCount { get; set; }

    [JsonPropertyName("clusterCount")]
    public int ClusterCount { get; set; }

    [JsonPropertyName("nodeManagerConfigured")]
    public bool NodeManagerConfigured { get; set; }

    [JsonPropertyName("nodeManagerType")]
    public string? NodeManagerType { get; set; }

    [JsonPropertyName("ohsInstances")]
    public int OhsInstances { get; set; }

    [JsonPropertyName("scanStatus")]
    public DiscoveryScanStatus ScanStatus { get; set; } = DiscoveryScanStatus.NotStarted;

    [JsonPropertyName("discoveredAtUtc")]
    public DateTimeOffset? DiscoveredAtUtc { get; set; }

    [JsonPropertyName("managedServers")]
    public List<ManagedServerDescriptor> ManagedServers { get; set; } = [];

    [JsonPropertyName("clusters")]
    public List<ClusterDescriptor> Clusters { get; set; } = [];

    [JsonPropertyName("reportsServers")]
    public List<ReportsServerDescriptor> ReportsServers { get; set; } = [];

    [JsonPropertyName("jvmArguments")]
    public List<string> JvmArguments { get; set; } = [];

    [JsonPropertyName("sslEnabled")]
    public bool SslEnabled { get; set; }

    [JsonPropertyName("sslProtocolSummary")]
    public string? SslProtocolSummary { get; set; }
}

public sealed class FormsReportsMetadataSnapshot
{
    [JsonPropertyName("moduleCount")]
    public int ModuleCount { get; set; }

    [JsonPropertyName("formCount")]
    public int FormCount { get; set; }

    [JsonPropertyName("reportCount")]
    public int ReportCount { get; set; }

    [JsonPropertyName("menuCount")]
    public int MenuCount { get; set; }

    [JsonPropertyName("usesWebUtil")]
    public bool UsesWebUtil { get; set; }

    [JsonPropertyName("webUtilModuleCount")]
    public int WebUtilModuleCount { get; set; }

    [JsonPropertyName("usesOracleGraphics")]
    public bool UsesOracleGraphics { get; set; }

    [JsonPropertyName("customPlsqlLibraries")]
    public int CustomPlsqlLibraries { get; set; }

    [JsonPropertyName("topModules")]
    public List<string> TopModules { get; set; } = [];

    [JsonPropertyName("configurationPath")]
    public string? ConfigurationPath { get; set; }
}

public sealed class MigrationReadinessSnapshot
{
    [JsonPropertyName("level")]
    public MigrationReadinessLevel Level { get; set; } = MigrationReadinessLevel.NotAssessed;

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("readinessPercent")]
    public double ReadinessPercent { get; set; }

    [JsonPropertyName("weightedRiskScore")]
    public double WeightedRiskScore { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "Compatibility analysis has not been run.";

    [JsonPropertyName("executiveSummary")]
    public string ExecutiveSummary { get; set; } = string.Empty;

    [JsonPropertyName("technicalSummary")]
    public string TechnicalSummary { get; set; } = string.Empty;

    [JsonPropertyName("complexity")]
    public MigrationComplexityKind Complexity { get; set; } = MigrationComplexityKind.Medium;

    [JsonPropertyName("effortCategory")]
    public MigrationEffortCategory EffortCategory { get; set; } = MigrationEffortCategory.Standard;

    [JsonPropertyName("blockerCount")]
    public int BlockerCount { get; set; }

    [JsonPropertyName("criticalCount")]
    public int CriticalCount { get; set; }

    [JsonPropertyName("highCount")]
    public int HighCount { get; set; }

    [JsonPropertyName("mediumCount")]
    public int MediumCount { get; set; }

    [JsonPropertyName("lowCount")]
    public int LowCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("assessedAtUtc")]
    public DateTimeOffset? AssessedAtUtc { get; set; }
}

public sealed class CompatibilityFinding
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public CompatibilitySeverity Severity { get; set; }

    [JsonPropertyName("category")]
    public CompatibilityRiskCategory Category { get; set; }

    [JsonPropertyName("remediation")]
    public string? Remediation { get; set; }

    [JsonPropertyName("blocksMigration")]
    public bool BlocksMigration { get; set; }
}

public sealed class MigrationValidationMessage
{
    [JsonPropertyName("severity")]
    public CompatibilitySeverity Severity { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;
}
