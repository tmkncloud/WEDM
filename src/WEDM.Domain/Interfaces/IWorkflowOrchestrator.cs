using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Orchestrates the ordered execution of deployment steps.
/// Responsible for step sequencing, retry policy, rollback triggers,
/// and progress reporting. The engine is stateless per invocation.
/// </summary>
public interface IWorkflowOrchestrator
{
    /// <summary>Build the ordered step list for the given configuration (no execution).</summary>
    IReadOnlyList<DeploymentStep> BuildStepPlan(DeploymentConfiguration config);

    /// <summary>Execute the workflow step by step with full error handling and retry.</summary>
    Task<DeploymentReport> RunAsync(
        DeploymentConfiguration config,
        IReadOnlyList<DeploymentStep> steps,
        CancellationToken cancellationToken = default);

    /// <summary>Execute a single named step (for retry / resume scenarios).</summary>
    Task<StepExecutionResult> RunStepAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempt rollback of all completed, rollback-capable steps in reverse execution order.
    /// Returns a <see cref="RollbackSummary"/> describing exactly which steps were reversed,
    /// which had no registered executor (requiring manual intervention), and which failed.
    /// Never throws — individual step failures are captured in the summary.
    /// </summary>
    Task<RollbackSummary> RollbackAsync(
        IReadOnlyList<DeploymentStep> steps,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);

    event EventHandler<DeploymentStep>? StepStarted;
    event EventHandler<DeploymentStep>? StepCompleted;
    event EventHandler<double>?          ProgressUpdated;
}
