namespace WEDM.Domain.Models;

/// <summary>Optional context for resumable workflow execution and checkpoint callbacks.</summary>
public sealed class DeploymentWorkflowRunContext
{
    public Guid SessionId { get; init; }

    public DeploymentSessionState? ResumeState { get; init; }

    public Func<DeploymentSessionState, CancellationToken, Task>? CheckpointAsync { get; init; }

    public Action? Heartbeat { get; init; }

    public static DeploymentWorkflowRunContext Fresh(Guid sessionId, Func<DeploymentSessionState, CancellationToken, Task>? checkpoint = null)
        => new() { SessionId = sessionId, CheckpointAsync = checkpoint };
}
