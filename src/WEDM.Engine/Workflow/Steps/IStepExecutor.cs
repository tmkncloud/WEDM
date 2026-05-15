using WEDM.Domain.Models;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>
/// Contract for individual step executors.
/// Each step executor encapsulates the automation logic for a single deployment action.
/// Implementing clean separation of concerns: the workflow engine drives sequencing,
/// each executor drives the actual automation (PowerShell, WLST, file ops, registry).
/// </summary>
public interface IStepExecutor
{
    /// <summary>
    /// Execute the step action.
    /// Implementations must be idempotent where possible — re-running should not corrupt state.
    /// All exceptions must be caught internally and returned as StepExecutionResult.Fail.
    /// </summary>
    Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory that resolves IStepExecutor instances by step name.
/// Backed by the DI container — all executors are registered on startup.
/// </summary>
public interface IStepExecutorFactory
{
    IStepExecutor? GetExecutor(string stepName);
    IStepExecutor? GetRollbackExecutor(string rollbackAction);
}

/// <summary>
/// DI-registered implementation of IStepExecutorFactory.
/// Uses named registrations from the service provider.
/// </summary>
public sealed class StepExecutorFactory : IStepExecutorFactory
{
    private readonly IReadOnlyDictionary<string, IStepExecutor> _executors;
    private readonly IReadOnlyDictionary<string, IStepExecutor> _rollbackExecutors;
    private readonly Func<string, IStepExecutor?> _fallbackForward;
    private readonly Func<string, IStepExecutor?> _fallbackRollback;

    public StepExecutorFactory(
        IReadOnlyDictionary<string, IStepExecutor> executors,
        IReadOnlyDictionary<string, IStepExecutor> rollbackExecutors,
        Func<string, IStepExecutor?>? fallbackForward = null,
        Func<string, IStepExecutor?>? fallbackRollback = null)
    {
        _executors           = executors;
        _rollbackExecutors   = rollbackExecutors;
        _fallbackForward    = fallbackForward ?? (_ => null);
        _fallbackRollback   = fallbackRollback ?? (_ => null);
    }

    public IStepExecutor? GetExecutor(string stepName)
        => _executors.TryGetValue(stepName, out var ex) ? ex : _fallbackForward(stepName);

    public IStepExecutor? GetRollbackExecutor(string rollbackAction)
    {
        if (_rollbackExecutors.TryGetValue(rollbackAction, out var ex))
            return ex;
        return _fallbackRollback?.Invoke(rollbackAction);
    }
}
