using System.Text.Json;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Remediation;

/// <summary>
/// Coordinates plan execution with checkpoint loading, attempt limits, and verification.
/// </summary>
public sealed class RemediationExecutionEngine
{
    private readonly IOraclePartialInstallRemediator _remediator;
    private readonly IRemediationVerificationService _verification;
    private readonly IRemediationReportBuilder     _reports;
    private readonly ILoggingService               _log;

    public RemediationExecutionEngine(
        IOraclePartialInstallRemediator remediator,
        IRemediationVerificationService verification,
        IRemediationReportBuilder reports,
        ILoggingService log)
    {
        _remediator   = remediator;
        _verification = verification;
        _reports      = reports;
        _log          = log;
    }

    public async Task<OracleRemediationResult> ExecuteAsync(
        DeploymentConfiguration config,
        OracleRemediationAssessment assessment,
        RemediationPlan plan,
        RemediationExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (plan.Actions.Count == 0)
        {
            _log.Info("[Remediation] No actions in plan — state may already be clean.", "Remediation");
            var emptyReport = _reports.BuildReport(assessment, plan, [], _verification.Verify(config), options);
            var alreadyClean = !assessment.RequiresRemediation || (emptyReport.Verification?.Passed ?? false);
            return new OracleRemediationResult
            {
                Success                 = alreadyClean,
                ContinuationRecommended = alreadyClean,
                Report                  = emptyReport,
            };
        }

        var checkpoint = LoadCheckpoint(config) ?? new RemediationCheckpoint
        {
            DeploymentId  = config.Id,
            AttemptNumber = config.CurrentInstallerContext?.AttemptNumber ?? 1,
        };

        _log.Info(
            $"[Remediation] Executing {plan.Actions.Count} action(s) " +
            $"(dryRun={options.DryRun}, classification={assessment.Classification})",
            "Remediation");

        var actionResults = await _remediator.ExecutePlanAsync(
            config, plan, options, checkpoint, cancellationToken);

        RemediationVerificationResult? verification = null;
        if (!options.DryRun)
            verification = _verification.Verify(config);

        var report = _reports.BuildReport(assessment, plan, actionResults, verification, options);
        report.Mode = config.OracleLifecycle.AutoRemediationMode;

        var continuation = !options.DryRun
                             && report.Success
                             && config.OracleLifecycle.AutoContinueAfterRemediation
                             && (verification?.CanProceedWithInstall ?? false);

        if (report.Success)
            _log.Info("[Remediation] Remediation completed successfully.", "Remediation");
        else
            _log.Warning("[Remediation] Remediation completed with failures or residual risk.", "Remediation");

        return new OracleRemediationResult
        {
            Success                  = report.Success,
            ContinuationRecommended  = continuation,
            Report                   = report,
        };
    }

    private static RemediationCheckpoint? LoadCheckpoint(DeploymentConfiguration config)
    {
        try
        {
            var path = Path.Combine(config.Paths.ReportsDirectory, $"remediation-checkpoint-{config.Id:N}.json");
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<RemediationCheckpoint>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
