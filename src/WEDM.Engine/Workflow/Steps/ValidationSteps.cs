using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Validation;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>
/// Workflow executor that re-runs the full prerequisite suite immediately before mutation.
/// This protects silent mode and resume mode from relying on stale wizard validation.
/// </summary>
public sealed class ValidatePrerequisitesStep : IStepExecutor
{
    private readonly IValidationEngine _validator;
    private readonly ILoggingService _log;

    public ValidatePrerequisitesStep(IValidationEngine validator, ILoggingService log)
    {
        _validator = validator;
        _log = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _validator.ValidateAllAsync(config, cancellationToken);
        sw.Stop();

        var summary = PrerequisiteValidationReporter.FormatSummary(result);
        _log.Info($"Prerequisite validation completed: {summary}", "Validation");

        if (result.CanProceed)
            return StepExecutionResult.Ok(summary, sw.Elapsed);

        var details = PrerequisiteValidationReporter.FormatDetailedBlockers(result);

        return StepExecutionResult.Fail(
            $"Prerequisite validation failed: {summary}",
            exitCode: 10,
            notes: details,
            retryRecommended: PrerequisiteValidationReporter.IsRetryRecommended(result),
            validation: result);
    }
}

/// <summary>
/// Verifies that required offline installer payloads exist before OUI is invoked.
/// Future phases will enforce signed checksum manifests; the current validator already
/// centralizes file presence checks so GUI and silent mode use the same rules.
/// </summary>
public sealed class ValidatePayloadIntegrityStep : IStepExecutor
{
    private readonly IValidationEngine _validator;
    private readonly ILoggingService _log;

    public ValidatePayloadIntegrityStep(IValidationEngine validator, ILoggingService log)
    {
        _validator = validator;
        _log = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _validator.ValidatePayloadIntegrityAsync(config, cancellationToken);
        sw.Stop();

        var summary = PrerequisiteValidationReporter.FormatSummary(result);
        _log.Info($"Payload validation completed: {summary}", "Validation");

        if (result.CanProceed)
            return StepExecutionResult.Ok(summary, sw.Elapsed);

        var details = PrerequisiteValidationReporter.FormatDetailedBlockers(result);

        return StepExecutionResult.Fail(
            $"Payload validation blocked deployment: {summary}",
            exitCode: 11,
            notes: details,
            retryRecommended: PrerequisiteValidationReporter.IsRetryRecommended(result),
            validation: result);
    }
}
