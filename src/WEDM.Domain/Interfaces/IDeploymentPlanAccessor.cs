using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Holds the currently executing deployment step plan so late-stage executors
/// (for example report generation) can read live step status without threading
/// large objects through every method signature.
/// </summary>
public interface IDeploymentPlanAccessor
{
    IReadOnlyList<DeploymentStep>? CurrentSteps { get; }

    void Bind(IReadOnlyList<DeploymentStep> steps);

    void Clear();
}
