using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Remediation;

/// <summary>
/// Proactive install gate: PartialInstall → Remediating → VerifiedClean → Installing.
/// Runs remediation before inventory hard-gate failure and before the OUI retry loop.
/// </summary>
public sealed class InstallRemediationOrchestrator : IInstallRemediationOrchestrator
{
    private readonly IOracleRemediationService _remediation;
    private readonly ILoggingService         _log;

    public InstallRemediationOrchestrator(
        IOracleRemediationService remediation,
        ILoggingService log)
    {
        _remediation = remediation;
        _log        = log;
    }

    public async Task<InstallRemediationGateResult> EnsureInstallReadyAsync(
        DeploymentConfiguration config,
        string stepName,
        int attemptNumber,
        IOracleInventoryService inventory,
        CancellationToken cancellationToken = default)
    {
        var session = config.RemediationSession;
        var mw      = config.Paths.MiddlewareHome;
        var inv     = config.Paths.OracleInventory;
        var attemptKey = $"{stepName}:a{attemptNumber}";

        _log.Info(
            $"[Remediation] Assessing install environment (step={stepName}, attempt={attemptNumber}, " +
            $"correlation={session.CorrelationId:N})...",
            "Remediation");

        var assessment = _remediation.Assess(config, stepName);

        if (!assessment.RequiresRemediation)
        {
            session.Phase = OracleRemediationPhase.NotRequired;
            var cleanValidation = inventory.ValidateForInstall(mw, inv);
            LogValidation(cleanValidation, "Pre-install");
            return new InstallRemediationGateResult
            {
                CanProceedToInstall = cleanValidation.CanProceed,
                Phase               = OracleRemediationPhase.NotRequired,
                PostValidation      = cleanValidation,
                FailureMessage      = cleanValidation.CanProceed
                    ? null
                    : BuildValidationFailureMessage(cleanValidation),
            };
        }

        session.Phase = OracleRemediationPhase.Detected;
        _log.Warning(
            $"[Remediation] Partial install detected (classification={assessment.Classification}, " +
            $"homeState={assessment.HomeState}).",
            "Remediation");

        if (assessment.Classification == OracleRemediationState.UnsafeToClean)
        {
            session.Phase = OracleRemediationPhase.UnsafeBlocked;
            var blocking = string.Join("; ", assessment.Safety.BlockingReasons);
            _log.Error(
                $"[Remediation] Unsafe to clean — automated remediation blocked. {blocking}",
                category: "Remediation");
            return Blocked(
                $"Oracle partial install detected but automated cleanup is unsafe. {blocking}",
                OracleRemediationPhase.UnsafeBlocked);
        }

        if (!_remediation.ShouldAutoRemediate(config, assessment))
        {
            session.Phase = OracleRemediationPhase.Skipped;
            _log.Warning(
                $"[Remediation] Partial install detected (state={assessment.HomeState}) but auto-remediation is " +
                $"disabled or requires confirmation (mode={config.OracleLifecycle.AutoRemediationMode}, " +
                $"canAuto={assessment.CanAutoRemediate}).",
                "Remediation");
            var blocked = inventory.ValidateForInstall(mw, inv);
            return Blocked(
                BuildValidationFailureMessage(blocked),
                OracleRemediationPhase.Skipped,
                blocked);
        }

        if (session.TotalExecutions >= config.OracleLifecycle.MaxRemediationAttempts)
        {
            session.Phase = OracleRemediationPhase.Failed;
            _log.Error(
                $"[Remediation] Max remediation attempts ({config.OracleLifecycle.MaxRemediationAttempts}) " +
                $"exceeded for this deployment (correlation={session.CorrelationId:N}).",
                category: "Remediation");
            return Blocked(
                "Maximum automatic remediation attempts exceeded. Resolve partial install artifacts manually.",
                OracleRemediationPhase.Failed);
        }

        if (session.ExecutedForStepAttempt.Contains(attemptKey))
        {
            _log.Info(
                $"[Remediation] Remediation already executed for {attemptKey}; re-validating only.",
                "Remediation");
            return await RevalidateAfterRemediationAsync(
                inventory, config, session, attemptKey, remediationExecuted: true, cancellationToken);
        }

        session.Phase = OracleRemediationPhase.Remediating;
        _log.Info("[Remediation] Starting automatic cleanup...", "Remediation");
        _log.Info(
            $"[Remediation] Safety classification: {assessment.Classification} " +
            $"(risk={assessment.Safety.Risk}, confidence={assessment.Safety.Confidence})",
            "Remediation");

        foreach (var action in assessment.RecommendedPlan?.Actions ?? [])
        {
            if (action.ActionType == RemediationActionType.DeleteDirectory)
                _log.Info($"[Remediation] Removing partial middleware home: {action.TargetPath}", "Remediation");
            else
                _log.Info($"[Remediation] Planned action: {action.ActionType} → {action.TargetPath}", "Remediation");
        }

        var result = await _remediation.ExecuteAsync(
            config,
            new RemediationExecutionOptions
            {
                DryRun          = false,
                Trigger         = stepName,
                ForceExecute    = true,
                AttemptNumber   = attemptNumber,
                CorrelationId   = session.CorrelationId,
            },
            cancellationToken);

        session.ExecutedForStepAttempt.Add(attemptKey);
        session.TotalExecutions++;

        foreach (var action in result.Report.ExecutedActions)
            _log.Info($"[Remediation] {action.ActionType}: {action.Outcome} — {action.Message}", "Remediation");

        if (!result.Success)
        {
            session.Phase = OracleRemediationPhase.Failed;
            var errors = result.Report.Errors.Count > 0
                ? string.Join("; ", result.Report.Errors)
                : string.Join("; ", result.Report.RemainingRisks);
            _log.Error($"[Remediation] Automatic cleanup failed. {errors}", category: "Remediation");
            return Blocked(
                $"Automatic partial-install remediation failed. {errors}",
                OracleRemediationPhase.Failed);
        }

        if (result.Report.Verification is { Passed: false } verification)
        {
            session.Phase = OracleRemediationPhase.Failed;
            _log.Error(
                $"[Remediation] Post-cleanup verification failed: {string.Join("; ", verification.Findings)}",
                category: "Remediation");
            return Blocked(
                "Remediation completed but verification did not confirm a clean install state.",
                OracleRemediationPhase.Failed);
        }

        session.Phase = OracleRemediationPhase.VerifiedClean;
        _log.Info("[Remediation] Verification passed", "Remediation");

        var gate = await RevalidateAfterRemediationAsync(
            inventory, config, session, attemptKey, remediationExecuted: true, cancellationToken);

        if (gate.CanProceedToInstall)
            _log.Info("[Remediation] Continuing InstallInfrastructure", "Remediation");

        return gate;
    }

    private Task<InstallRemediationGateResult> RevalidateAfterRemediationAsync(
        IOracleInventoryService inventory,
        DeploymentConfiguration config,
        OracleRemediationSessionState session,
        string attemptKey,
        bool remediationExecuted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mw  = config.Paths.MiddlewareHome;
        var inv = config.Paths.OracleInventory;

        var postValidation = inventory.ValidateForInstall(mw, inv);
        LogValidation(postValidation, "Post-remediation");

        if (!postValidation.CanProceed && remediationExecuted)
        {
            session.Phase = OracleRemediationPhase.Failed;
            return Task.FromResult(Blocked(
                BuildValidationFailureMessage(postValidation),
                OracleRemediationPhase.Failed,
                postValidation,
                remediationExecuted));
        }

        if (postValidation.CanProceed)
            session.Phase = OracleRemediationPhase.VerifiedClean;

        return Task.FromResult(new InstallRemediationGateResult
        {
            CanProceedToInstall = postValidation.CanProceed,
            Phase               = session.Phase,
            PostValidation      = postValidation,
            RemediationExecuted = remediationExecuted,
            FailureMessage      = postValidation.CanProceed ? null : BuildValidationFailureMessage(postValidation),
        });
    }

    private void LogValidation(OracleInventoryValidationResult result, string phase)
    {
        _log.Info(
            $"[{phase}] Oracle inventory: state={result.HomeState} canProceed={result.CanProceed}",
            "Remediation");
        foreach (var f in result.Findings)
            _log.Info($"  [{phase}] {f}", "Remediation");
        foreach (var r in result.RemediationSteps)
            _log.Warning($"  [{phase}] REMEDIATION: {r}", "Remediation");
    }

    private static string BuildValidationFailureMessage(OracleInventoryValidationResult validation)
    {
        var findings    = string.Join(" | ", validation.Findings);
        var remediation = string.Join(" | ", validation.RemediationSteps);
        return $"Oracle inventory pre-install check failed (state: {validation.HomeState}). " +
               $"{findings} Remediation: {remediation}";
    }

    private static InstallRemediationGateResult Blocked(
        string message,
        OracleRemediationPhase phase,
        OracleInventoryValidationResult? validation = null,
        bool remediationExecuted = false) =>
        new()
        {
            CanProceedToInstall = false,
            Phase               = phase,
            PostValidation      = validation,
            FailureMessage      = message,
            RemediationExecuted = remediationExecuted,
        };
}
