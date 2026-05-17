using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

public interface IPartialInstallClassifier
{
    OracleRemediationAssessment Classify(DeploymentConfiguration config, RemediationDiscoveryContext? context = null);
}

public interface IOracleHomeSafetyAnalyzer
{
    SafetyAnalysisResult Analyze(RemediationDiscoveryContext context, OracleRemediationState classification);
}

public interface IRemediationPlanBuilder
{
    RemediationPlan Build(
        DeploymentConfiguration config,
        OracleRemediationAssessment assessment,
        SafetyAnalysisResult safety);
}

public interface IOraclePartialInstallRemediator
{
    Task<IReadOnlyList<RemediationActionResult>> ExecutePlanAsync(
        DeploymentConfiguration config,
        RemediationPlan plan,
        RemediationExecutionOptions options,
        RemediationCheckpoint? checkpoint,
        CancellationToken cancellationToken = default);
}

public interface IRemediationVerificationService
{
    RemediationVerificationResult Verify(DeploymentConfiguration config);
}

public interface IRemediationReportBuilder
{
    OracleRemediationReport BuildReport(
        OracleRemediationAssessment assessment,
        RemediationPlan plan,
        IReadOnlyList<RemediationActionResult> actionResults,
        RemediationVerificationResult? verification,
        RemediationExecutionOptions options,
        Guid correlationId = default,
        OracleRemediationPhase phase = OracleRemediationPhase.NotRequired);
}

public interface IOracleRemediationService
{
    OracleRemediationAssessment Assess(DeploymentConfiguration config, string? triggerStep = null);

    Task<OracleRemediationResult> ExecuteAsync(
        DeploymentConfiguration config,
        RemediationExecutionOptions options,
        CancellationToken cancellationToken = default);

    bool ShouldAutoRemediate(DeploymentConfiguration config, OracleRemediationAssessment assessment);
}

/// <summary>
/// Proactive install-time remediation gate: PartialInstall → Remediating → VerifiedClean → Installing.
/// </summary>
public interface IInstallRemediationOrchestrator
{
    Task<InstallRemediationGateResult> EnsureInstallReadyAsync(
        DeploymentConfiguration config,
        string stepName,
        int attemptNumber,
        IOracleInventoryService inventory,
        CancellationToken cancellationToken = default);
}
