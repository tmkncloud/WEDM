using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

public sealed class TransformationExecutionResult
{
    [JsonPropertyName("workspacePath")]
    public string WorkspacePath { get; set; } = string.Empty;

    [JsonPropertyName("stages")]
    public List<TransformationStageResult> Stages { get; set; } = [];

    [JsonPropertyName("artifacts")]
    public List<GeneratedTransformationArtifact> Artifacts { get; set; } = [];

    [JsonPropertyName("remediations")]
    public List<RemediationRecommendation> Remediations { get; set; } = [];

    [JsonPropertyName("configTransformations")]
    public List<ConfigTransformationRecord> ConfigTransformations { get; set; } = [];

    [JsonPropertyName("validation")]
    public TransformationValidationSummary Validation { get; set; } = new();

    [JsonPropertyName("migrationPlan")]
    public MigrationPlanDocument MigrationPlan { get; set; } = new();

    [JsonPropertyName("formsModernization")]
    public FormsModernizationSnapshot FormsModernization { get; set; } = new();

    [JsonPropertyName("reportsModernization")]
    public ReportsModernizationSnapshot ReportsModernization { get; set; } = new();

    [JsonPropertyName("confidence")]
    public TransformationConfidenceKind Confidence { get; set; } = TransformationConfidenceKind.NotAssessed;

    [JsonPropertyName("planPreview")]
    public string PlanPreview { get; set; } = string.Empty;

    [JsonPropertyName("totalDurationMs")]
    public long TotalDurationMs { get; set; }

    [JsonPropertyName("completed")]
    public bool Completed { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];
}

public sealed class TransformationStageResult
{
    [JsonPropertyName("stage")]
    public TransformationStageKind Stage { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public TransformationStageStatus Status { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class GeneratedTransformationArtifact
{
    [JsonPropertyName("kind")]
    public TransformationArtifactKind Kind { get; set; }

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

public sealed class ConfigTransformationRecord
{
    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("outputPath")]
    public string OutputPath { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("originalExcerpt")]
    public string? OriginalExcerpt { get; set; }

    [JsonPropertyName("transformedExcerpt")]
    public string? TransformedExcerpt { get; set; }

    [JsonPropertyName("remediationNotes")]
    public List<string> RemediationNotes { get; set; } = [];
}

public sealed class RemediationRecommendation
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public CompatibilityRiskCategory Category { get; set; }

    [JsonPropertyName("severity")]
    public CompatibilitySeverity Severity { get; set; }

    [JsonPropertyName("manualRequired")]
    public bool ManualRequired { get; set; }

    [JsonPropertyName("estimatedEffortHours")]
    public double? EstimatedEffortHours { get; set; }
}

public sealed class TransformationValidationSummary
{
    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("confidence")]
    public TransformationConfidenceKind Confidence { get; set; }

    [JsonPropertyName("blockerCount")]
    public int BlockerCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("messages")]
    public List<TransformationValidationMessage> Messages { get; set; } = [];
}

public sealed class TransformationValidationMessage
{
    [JsonPropertyName("severity")]
    public TransformationValidationSeverity Severity { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("artifactPath")]
    public string? ArtifactPath { get; set; }
}

public sealed class MigrationPlanDocument
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("upgradePath")]
    public string UpgradePath { get; set; } = string.Empty;

    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = string.Empty;

    [JsonPropertyName("estimatedEffortCategory")]
    public string EstimatedEffortCategory { get; set; } = string.Empty;

    [JsonPropertyName("stages")]
    public List<MigrationPlanStage> Stages { get; set; } = [];

    [JsonPropertyName("prerequisites")]
    public List<string> Prerequisites { get; set; } = [];

    [JsonPropertyName("remediationTasks")]
    public List<string> RemediationTasks { get; set; } = [];

    [JsonPropertyName("rollbackSteps")]
    public List<string> RollbackSteps { get; set; } = [];

    [JsonPropertyName("cutoverSteps")]
    public List<string> CutoverSteps { get; set; } = [];

    [JsonPropertyName("postMigrationValidation")]
    public List<string> PostMigrationValidation { get; set; } = [];

    [JsonPropertyName("operatorSummary")]
    public string OperatorSummary { get; set; } = string.Empty;
}

public sealed class MigrationPlanStage
{
    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("estimatedHours")]
    public double EstimatedHours { get; set; }
}

public sealed class FormsModernizationSnapshot
{
    [JsonPropertyName("moduleDependencies")]
    public List<ModuleDependencyRecord> ModuleDependencies { get; set; } = [];

    [JsonPropertyName("pllLibraries")]
    public List<string> PllLibraries { get; set; } = [];

    [JsonPropertyName("olbReferences")]
    public List<string> OlbReferences { get; set; } = [];

    [JsonPropertyName("triggerSummary")]
    public string TriggerSummary { get; set; } = string.Empty;

    [JsonPropertyName("webUtilClassification")]
    public string WebUtilClassification { get; set; } = string.Empty;

    [JsonPropertyName("complexityScore")]
    public int ComplexityScore { get; set; }

    [JsonPropertyName("blockers")]
    public List<string> Blockers { get; set; } = [];

    [JsonPropertyName("manualRemediationCandidates")]
    public List<string> ManualRemediationCandidates { get; set; } = [];
}

public sealed class ReportsModernizationSnapshot
{
    [JsonPropertyName("reportInventoryCount")]
    public int ReportInventoryCount { get; set; }

    [JsonPropertyName("serverDependencies")]
    public List<string> ServerDependencies { get; set; } = [];

    [JsonPropertyName("outputFormats")]
    public List<string> OutputFormats { get; set; } = [];

    [JsonPropertyName("customRuntimeDetected")]
    public bool CustomRuntimeDetected { get; set; }

    [JsonPropertyName("readinessSummary")]
    public string ReadinessSummary { get; set; } = string.Empty;

    [JsonPropertyName("unsupportedFeatures")]
    public List<string> UnsupportedFeatures { get; set; } = [];

    [JsonPropertyName("migrationCandidates")]
    public List<string> MigrationCandidates { get; set; } = [];
}

public sealed class ModuleDependencyRecord
{
    [JsonPropertyName("moduleName")]
    public string ModuleName { get; set; } = string.Empty;

    [JsonPropertyName("dependsOn")]
    public List<string> DependsOn { get; set; } = [];
}

public sealed class TransformationWorkspaceManifest
{
    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("sourceRelease")]
    public string SourceRelease { get; set; } = string.Empty;

    [JsonPropertyName("targetRelease")]
    public string TargetRelease { get; set; } = string.Empty;

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = string.Empty;

    [JsonPropertyName("artifactCount")]
    public int ArtifactCount { get; set; }

    [JsonPropertyName("workspacePath")]
    public string WorkspacePath { get; set; } = string.Empty;
}

public sealed class TransformationExecutionOptions
{
    [JsonPropertyName("workspaceRoot")]
    public string? WorkspaceRoot { get; set; }

    [JsonPropertyName("targetDomainHome")]
    public string? TargetDomainHome { get; set; }
}
