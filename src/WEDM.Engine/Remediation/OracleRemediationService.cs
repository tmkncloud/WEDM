using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Remediation;

public sealed class OracleRemediationService : IOracleRemediationService
{
    private readonly IPartialInstallClassifier   _classifier;
    private readonly IRemediationPlanBuilder     _planBuilder;
    private readonly RemediationExecutionEngine  _engine;
    private readonly ILoggingService             _log;

    public OracleRemediationService(
        IPartialInstallClassifier classifier,
        IRemediationPlanBuilder planBuilder,
        RemediationExecutionEngine engine,
        ILoggingService log)
    {
        _classifier   = classifier;
        _planBuilder  = planBuilder;
        _engine       = engine;
        _log          = log;
    }

    public OracleRemediationAssessment Assess(DeploymentConfiguration config, string? triggerStep = null)
    {
        var baseAssessment = _classifier.Classify(config);
        var plan           = _planBuilder.Build(config, baseAssessment, baseAssessment.Safety);
        var assessment = new OracleRemediationAssessment
        {
            Classification   = baseAssessment.Classification,
            HomeState        = baseAssessment.HomeState,
            Safety           = baseAssessment.Safety,
            Issues           = baseAssessment.Issues,
            RequiresRemediation = baseAssessment.RequiresRemediation,
            CanAutoRemediate = baseAssessment.CanAutoRemediate,
            RecommendedPlan  = plan,
        };
        config.LastRemediationAssessment = assessment;
        return assessment;
    }

    public async Task<OracleRemediationResult> ExecuteAsync(
        DeploymentConfiguration config,
        RemediationExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var assessment = config.LastRemediationAssessment ?? Assess(config, options.Trigger);
        var plan       = assessment.RecommendedPlan
                         ?? _planBuilder.Build(config, assessment, assessment.Safety);

        var mayExecute = options.DryRun
                           || options.ForceExecute
                           || options.ForceUnsafe
                           || ShouldAutoRemediate(config, assessment)
                           || (plan.CanAutoExecute && assessment.CanAutoRemediate);

        if (!mayExecute && assessment.RequiresRemediation)
        {
            _log.Warning("[Remediation] Plan is not eligible for automatic execution.", "Remediation");
            return new OracleRemediationResult
            {
                Success = false,
                Phase   = OracleRemediationPhase.Skipped,
                Report  = new OracleRemediationReport
                {
                    DryRun         = options.DryRun,
                    Trigger        = options.Trigger,
                    Classification = assessment.Classification,
                    Safety         = assessment.Safety,
                    Plan           = plan,
                    Phase          = OracleRemediationPhase.Skipped,
                    DetectedIssues = assessment.Issues,
                    RemainingRisks = assessment.Safety.BlockingReasons.ToList(),
                },
            };
        }

        var result = await _engine.ExecuteAsync(config, assessment, plan, options, cancellationToken);
        config.RemediationReports.Add(result.Report);
        return result;
    }

    public bool ShouldAutoRemediate(DeploymentConfiguration config, OracleRemediationAssessment assessment)
    {
        var lc = config.OracleLifecycle;
        if (!lc.EnableAutoRemediation || lc.AutoRemediationMode == AutoRemediationMode.Disabled)
            return false;

        if (!assessment.RequiresRemediation)
            return false;

        if (lc.AutoRemediationMode == AutoRemediationMode.SuggestOnly)
            return false;

        if (lc.AutoRemediationMode == AutoRemediationMode.ConfirmRequired)
            return false; // UI must call ExecuteAsync explicitly

        if (lc.SafeCleanupOnly && !assessment.CanAutoRemediate)
            return false;

        return lc.AutoRemediationMode is AutoRemediationMode.AutomaticSafeOnly or AutoRemediationMode.Aggressive;
    }
}
