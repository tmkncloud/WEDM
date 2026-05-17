using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

public enum OracleRemediationPhase
{
    NotRequired = 0,
    Detected = 1,
    Assessed = 2,
    Remediating = 3,
    VerifiedClean = 4,
    Failed = 5,
    UnsafeBlocked = 6,
    Skipped = 7,
}

public sealed class OracleRemediationSessionState
{
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public OracleRemediationPhase Phase { get; set; } = OracleRemediationPhase.NotRequired;
    public HashSet<string> ExecutedForStepAttempt { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int TotalExecutions { get; set; }
}

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
    public int StaleInstallActivityMinutes { get; init; } = 15;
}

public sealed class SafetyAnalysisResult
{
    public bool IsSafeToRemediate { get; init; }
    public RemediationRiskLevel Risk { get; init; } = RemediationRiskLevel.Medium;
    public RemediationConfidence Confidence { get; init; } = RemediationConfidence.Medium;
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<string> BlockingReasons { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
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
    public OracleRemediationState RemediationStateAfter { get; init; } = OracleRemediationState.Unknown;
    public bool CanProceedWithInstall { get; init; }
    public bool MiddlewareDirectoryCleared { get; init; }
    public bool InventoryClean { get; init; }
    public bool NoActiveProcesses { get; init; }
    public bool NoActiveLocks { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = [];
}

public sealed class OracleRemediationReport
{
    public Guid ReportId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool DryRun { get; init; }
    public AutoRemediationMode Mode { get; set; }
    public string Trigger { get; init; } = string.Empty;
    public OracleRemediationState Classification { get; init; } = OracleRemediationState.Unknown;
    public OracleHomeState OriginalHomeState { get; init; } = OracleHomeState.Unknown;
    public OracleRemediationPhase Phase { get; set; } = OracleRemediationPhase.NotRequired;
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
    public List<string> Errors { get; init; } = [];
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
    public OracleRemediationPhase Phase { get; init; } = OracleRemediationPhase.NotRequired;
    public OracleRemediationReport Report { get; init; } = new();
}

public sealed class InstallRemediationGateResult
{
    public bool CanProceedToInstall { get; init; }
    public OracleRemediationPhase Phase { get; init; } = OracleRemediationPhase.NotRequired;
    public OracleInventoryValidationResult? PostValidation { get; init; }
    public string? FailureMessage { get; init; }
    public bool RemediationExecuted { get; init; }
}

public sealed class RemediationExecutionOptions
{
    public bool DryRun { get; init; }
    public string Trigger { get; init; } = string.Empty;
    public bool ForceUnsafe { get; init; }
    public bool ForceExecute { get; init; }
    public int AttemptNumber { get; init; } = 1;
    public Guid? CorrelationId { get; init; }
}

/// <summary>Persisted checkpoint for crash-tolerant remediation retries.</summary>
public sealed class RemediationCheckpoint
{
    [JsonPropertyName("deploymentId")]
    public Guid DeploymentId { get; init; }

    [JsonPropertyName("attemptNumber")]
    public int AttemptNumber { get; init; }

    [JsonPropertyName("correlationId")]
    public Guid CorrelationId { get; init; }

    [JsonPropertyName("completedActionKeys")]
    public List<string> CompletedActionKeys { get; set; } = [];

    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
}
