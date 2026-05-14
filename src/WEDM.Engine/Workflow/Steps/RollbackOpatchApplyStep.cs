using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>
/// Rollback hook for failed OPatch apply. Oracle patch rollback is environment-specific;
/// WEDM records the need for manual intervention rather than guessing patch IDs.
/// </summary>
public sealed class RollbackOpatchApplyStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public RollbackOpatchApplyStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        _log.Warning(
            "OPatch apply rollback is not automated. Use Oracle OPatch rollback for the last patch set, restore from a filesystem backup, or re-image the Oracle Home per your change policy.",
            "Rollback.OPatch");
        return Task.FromResult(StepExecutionResult.Ok("Documented manual OPatch rollback required."));
    }
}
