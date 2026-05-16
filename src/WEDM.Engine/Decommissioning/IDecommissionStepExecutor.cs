using WEDM.Domain.Models;

namespace WEDM.Engine.Decommissioning;

public interface IDecommissionStepExecutor
{
    Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default);
}
