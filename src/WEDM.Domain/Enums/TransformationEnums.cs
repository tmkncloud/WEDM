namespace WEDM.Domain.Enums;

public enum TransformationStageKind
{
    WorkspaceInitialization,
    FormsModernizationAnalysis,
    ReportsModernizationAnalysis,
    DomainTransformation,
    JvmModernization,
    NodeManagerTransformation,
    SslHardeningPreparation,
    FormsConfigTransformation,
    ReportsConfigTransformation,
    WlstGeneration,
    RemediationGeneration,
    ArtifactValidation,
    MigrationPlanGeneration,
    ManifestFinalization,
}

public enum TransformationStageStatus
{
    Pending,
    Running,
    Completed,
    Skipped,
    Failed,
}

public enum TransformationConfidenceKind
{
    Low,
    Moderate,
    High,
    NotAssessed,
}

public enum TransformationArtifactKind
{
    WlstScript,
    TransformedConfig,
    RemediationReport,
    MigrationPlan,
    ValidationSummary,
    DiscoverySnapshot,
    Manifest,
    ComparisonReport,
    ModernizationReport,
    RollbackNotes,
}

public enum TransformationValidationSeverity
{
    Informational,
    Warning,
    Error,
    Blocker,
}
