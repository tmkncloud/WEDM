namespace WEDM.Domain.Enums;

/// <summary>Top-level WEDM operation selected on the first wizard screen.</summary>
public enum WedmOperationMode
{
    None = 0,
    DeployNewEnvironment,
    UpgradeMigrateExisting,
}

/// <summary>Oracle Forms / middleware release families supported for migration source/target selection.</summary>
public enum MiddlewareReleaseKind
{
    Unknown = 0,
    Forms6i,
    Forms10g,
    Forms11g,
    Forms12c,
    Forms14c,
}

/// <summary>How aggressively the migration automation applies changes.</summary>
public enum MigrationStrategyKind
{
    ParallelRun,
    InPlaceUpgrade,
    SideBySideCutover,
    PhasedModuleMigration,
    LiftAndShiftReplatform,
}

/// <summary>Compatibility finding severity for enterprise risk dashboards.</summary>
public enum CompatibilitySeverity
{
    Informational,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>Category buckets used by the compatibility assessment engine.</summary>
public enum CompatibilityRiskCategory
{
    JvmConfiguration,
    UnsupportedLibraries,
    WebUtil,
    ReportsRuntime,
    FormsConfiguration,
    Authentication,
    NodeManager,
    Topology,
    Licensing,
    SecurityHardening,
    CustomIntegration,
    General,
}

/// <summary>Overall migration readiness band derived from assessment scoring.</summary>
public enum MigrationReadinessLevel
{
    NotAssessed,
    Blocked,
    HighRisk,
    ModerateRisk,
    ReadyWithRemediation,
    Ready,
}

/// <summary>Discovery scan lifecycle for source environment analysis.</summary>
public enum DiscoveryScanStatus
{
    NotStarted,
    InProgress,
    Completed,
    Partial,
    Failed,
}

/// <summary>Structured discovery pipeline stages.</summary>
public enum DiscoveryStageKind
{
    InventoryScan,
    MiddlewareHomeAnalysis,
    DomainAnalysis,
    FormsDiscovery,
    ReportsDiscovery,
    PatchInventory,
    CompatibilityEvaluation,
    ReadinessScoring,
}

public enum DiscoveryStageStatus
{
    Pending,
    Running,
    Completed,
    Skipped,
    Failed,
}

/// <summary>Overall modernization complexity band for executive reporting.</summary>
public enum MigrationComplexityKind
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>Estimated migration effort category for planning and staffing models.</summary>
public enum MigrationEffortCategory
{
    Short,
    Standard,
    Extended,
    Enterprise,
}
