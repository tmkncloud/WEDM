using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Deployment;

/// <summary>
/// Thread-safe holder for the active workflow step list (singleton per process).
/// </summary>
public sealed class DeploymentPlanAccessor : IDeploymentPlanAccessor
{
    private readonly object _lock = new();
    private IReadOnlyList<DeploymentStep>? _steps;

    public IReadOnlyList<DeploymentStep>? CurrentSteps
    {
        get
        {
            lock (_lock) return _steps;
        }
    }

    public void Bind(IReadOnlyList<DeploymentStep> steps)
    {
        lock (_lock) _steps = steps;
    }

    public void Clear()
    {
        lock (_lock) _steps = null;
    }
}
