using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

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

        var summary = $"{result.PassCount} passed, {result.WarnCount} warnings, {result.ErrorCount} errors, {result.Fatals} fatal";
        _log.Info($"Prerequisite validation completed: {summary}", "Validation");

        return result.CanProceed
            ? StepExecutionResult.Ok(summary, sw.Elapsed)
            : StepExecutionResult.Fail($"Prerequisite validation failed: {summary}", 10);
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

        var summary = $"{result.PassCount} payload checks passed, {result.ErrorCount} errors";
        _log.Info($"Payload validation completed: {summary}", "Validation");

        return result.CanProceed
            ? StepExecutionResult.Ok(summary, sw.Elapsed)
            : StepExecutionResult.Fail($"Payload validation failed: {summary}", 11);
    }
}
