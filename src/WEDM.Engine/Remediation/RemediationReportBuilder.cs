using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Remediation;

public sealed class RemediationReportBuilder : IRemediationReportBuilder
{
    public OracleRemediationReport BuildReport(
        OracleRemediationAssessment assessment,
        RemediationPlan plan,
        IReadOnlyList<RemediationActionResult> actionResults,
        RemediationVerificationResult? verification,
        RemediationExecutionOptions options,
        Guid correlationId = default,
        OracleRemediationPhase phase = OracleRemediationPhase.NotRequired)
    {
        var deleted = actionResults
            .Where(r => r.Outcome is RemediationExecutionOutcome.Succeeded or RemediationExecutionOutcome.DryRun)
            .Where(r => r.ActionType is RemediationActionType.DeleteDirectory
                or RemediationActionType.DeleteExtractionFolder
                or RemediationActionType.DeleteRetryTempDirectory
                or RemediationActionType.DeleteFile
                or RemediationActionType.RemoveStaleLockFile)
            .Select(r => r.TargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var detached = actionResults
            .Where(r => r.ActionType == RemediationActionType.DetachInventoryHome && r.Outcome == RemediationExecutionOutcome.Succeeded)
            .Select(r => r.TargetPath)
            .ToList();

        var failed = actionResults.Count(r => r.Outcome == RemediationExecutionOutcome.Failed);
        var success = failed == 0
                      && (verification?.Passed ?? true)
                      && (plan.Actions.Count > 0 || verification?.Passed == true);

        var remaining = new List<string>();
        if (verification is { Passed: false })
            remaining.AddRange(verification.Findings.Where(f => f.Contains("BLOCKED", StringComparison.OrdinalIgnoreCase)));
        if (assessment.Safety.BlockingReasons.Count > 0)
            remaining.AddRange(assessment.Safety.BlockingReasons);

        return new OracleRemediationReport
        {
            CorrelationId           = correlationId != Guid.Empty
                ? correlationId
                : options.CorrelationId ?? Guid.Empty,
            Phase                   = phase,
            DryRun                  = options.DryRun,
            Trigger                 = options.Trigger,
            Classification          = assessment.Classification,
            OriginalHomeState       = assessment.HomeState,
            Safety                  = assessment.Safety,
            Plan                    = plan,
            Success                 = success,
            DetectedIssues          = assessment.Issues,
            ExecutedActions         = actionResults,
            DeletedPaths            = deleted,
            DetachedInventoryEntries = detached,
            Verification            = verification,
            RemainingRisks          = remaining,
            CompletedAt             = DateTimeOffset.UtcNow,
        };
    }
}
