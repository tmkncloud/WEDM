using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

public sealed class RemediationDiscoveryContext
{
    public string MiddlewareHome { get; init; } = string.Empty;
    public string OracleInventoryPath { get; init; } = string.Empty;
    public string TempDirectory { get; init; } = string.Empty;
    public string? ExtractionDirectory { get; init; }
    public string? ResponseFilePath { get; init; }
    public string? SnapshotDirectory { get; init; }
    public string? ReportsDirectory { get; init; }
    public string? TriggerStep { get; init; }
    public int AttemptNumber { get; init; } = 1;
    public InstallerFailureClass? PreviousFailureClass { get; init; }
    public OracleHomeState DetectedHomeState { get; init; } = OracleHomeState.Unknown;
    public OracleCentralInventoryState InventoryState { get; init; } = OracleCentralInventoryState.Healthy;
}

public sealed class SafetyAnalysisResult
{
    public bool IsSafeToRemediate { get; init; }
    public RemediationRiskLevel Risk { get; init; } = RemediationRiskLevel.Medium;
    public RemediationConfidence Confidence { get; init; } = RemediationConfidence.Medium;
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<string> BlockingReasons { get; init; } = [];
    public string Recommendation { get; init; } = string.Empty;
}

public sealed class RemediationAction
{
    public RemediationActionType ActionType { get; init; }
    public string TargetPath { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public RemediationRiskLevel Risk { get; init; } = RemediationRiskLevel.Low;
    public bool RequiresElevation { get; init; }
    public bool IsIdempotent { get; init; } = true;
}

public sealed class RemediationPlan
{
    public OracleRemediationState Classification { get; init; } = OracleRemediationState.Unknown;
    public SafetyAnalysisResult Safety { get; init; } = new();
    public IReadOnlyList<RemediationAction> Actions { get; init; } = [];
    public bool CanAutoExecute { get; init; }
    public bool CanContinueDeploymentAfterSuccess { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class RemediationActionResult
{
    public RemediationActionType ActionType { get; init; }
    public string TargetPath { get; init; } = string.Empty;
    public RemediationExecutionOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class RemediationVerificationResult
{
    public bool Passed { get; init; }
    public OracleHomeState HomeStateAfter { get; init; } = OracleHomeState.Unknown;
    public bool CanProceedWithInstall { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = [];
}

public sealed class OracleRemediationReport
{
    public Guid ReportId { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool DryRun { get; init; }
    public AutoRemediationMode Mode { get; set; }
    public string Trigger { get; init; } = string.Empty;
    public OracleRemediationState Classification { get; init; } = OracleRemediationState.Unknown;
    public OracleHomeState OriginalHomeState { get; init; } = OracleHomeState.Unknown;
    public SafetyAnalysisResult Safety { get; init; } = new();
    public RemediationPlan? Plan { get; init; }
    public bool Success { get; set; }
    public IReadOnlyList<RemediationIssue> DetectedIssues { get; init; } = [];
    public IReadOnlyList<RemediationActionResult> ExecutedActions { get; init; } = [];
    public IReadOnlyList<string> DeletedPaths { get; init; } = [];
    public IReadOnlyList<string> DetachedInventoryEntries { get; init; } = [];
    public IReadOnlyList<string> StoppedProcesses { get; init; } = [];
    public RemediationVerificationResult? Verification { get; init; }
    public IReadOnlyList<string> RemainingRisks { get; init; } = [];
    public IReadOnlyList<string> ManualFollowUps { get; init; } = [];
}

public sealed class RemediationIssue
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public OracleRemediationState State { get; init; } = OracleRemediationState.Unknown;
    public RemediationRiskLevel Risk { get; init; } = RemediationRiskLevel.Medium;
}

public sealed class OracleRemediationAssessment
{
    public OracleRemediationState Classification { get; init; } = OracleRemediationState.Unknown;
    public OracleHomeState HomeState { get; init; } = OracleHomeState.Unknown;
    public SafetyAnalysisResult Safety { get; init; } = new();
    public RemediationPlan? RecommendedPlan { get; init; }
    public IReadOnlyList<RemediationIssue> Issues { get; init; } = [];
    public bool RequiresRemediation { get; init; }
    public bool CanAutoRemediate { get; init; }
}

public sealed class OracleRemediationResult
{
    public bool Success { get; init; }
    public bool ContinuationRecommended { get; init; }
    public OracleRemediationReport Report { get; init; } = new();
}

public sealed class RemediationExecutionOptions
{
    public bool DryRun { get; init; }
    public string Trigger { get; init; } = string.Empty;
    public bool ForceUnsafe { get; init; }
}

/// <summary>Persisted checkpoint for crash-tolerant remediation retries.</summary>
public sealed class RemediationCheckpoint
{
    [JsonPropertyName("deploymentId")]
    public Guid DeploymentId { get; init; }

    [JsonPropertyName("attemptNumber")]
    public int AttemptNumber { get; init; }

    [JsonPropertyName("completedActionKeys")]
    public List<string> CompletedActionKeys { get; set; } = [];

    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
}
