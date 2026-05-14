using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

/// <summary>
/// Represents a single atomic step within a deployment workflow.
/// Steps are orchestrated by the DeploymentWorkflowEngine and executed in order.
/// Each step is independently retryable, loggable, and rollback-aware.
/// </summary>
public sealed class DeploymentStep
{
    public Guid   Id          { get; init; } = Guid.NewGuid();
    public int    Sequence    { get; init; }         // Execution order (1-based)
    public string Name        { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category    { get; init; } = string.Empty;  // Prerequisites | Install | Configure | Validate

    public StepStatus Status           { get; set; } = StepStatus.Pending;
    public DateTimeOffset? StartedAt   { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public TimeSpan? Duration => CompletedAt.HasValue && StartedAt.HasValue
        ? CompletedAt.Value - StartedAt.Value
        : null;

    public int    AttemptCount { get; set; }
    public int    MaxRetries   { get; init; } = 2;
    public bool   CanRetry     => AttemptCount < MaxRetries && Status == StepStatus.Failed;
    public bool   IsRequired   { get; init; } = true;
    public bool   CanRollback  { get; init; } = true;

    public string OutputLog    { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int    ExitCode     { get; set; } = -1;

    // Progress: 0–100
    public double ProgressPercent { get; set; }

    // Rollback action (name of rollback script/command for this step)
    public string? RollbackAction { get; init; }

    public void MarkStarted()
    {
        Status     = StepStatus.Running;
        StartedAt  = DateTimeOffset.UtcNow;
        AttemptCount++;
    }

    public void MarkSucceeded(string output = "")
    {
        Status          = StepStatus.Succeeded;
        CompletedAt     = DateTimeOffset.UtcNow;
        OutputLog       = output;
        ExitCode        = 0;
        ProgressPercent = 100;
    }

    public void MarkFailed(string error, int exitCode = 1)
    {
        Status       = StepStatus.Failed;
        CompletedAt  = DateTimeOffset.UtcNow;
        ErrorMessage = error;
        ExitCode     = exitCode;
    }

    public void MarkSkipped(string reason = "")
    {
        Status      = StepStatus.Skipped;
        OutputLog   = reason;
        ExitCode    = 0;
        CompletedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Represents the result of executing a step's action — returned by automation executors.
/// </summary>
public sealed class StepExecutionResult
{
    public bool     Success    { get; init; }
    public int      ExitCode   { get; init; }
    public string   Output     { get; init; } = string.Empty;
    public string   Error      { get; init; } = string.Empty;
    public TimeSpan Duration   { get; init; }
    public Exception? Exception { get; init; }

    public static StepExecutionResult Ok(string output = "", TimeSpan duration = default)
        => new() { Success = true, ExitCode = 0, Output = output, Duration = duration };

    public static StepExecutionResult Fail(string error, int exitCode = 1, Exception? ex = null)
        => new() { Success = false, ExitCode = exitCode, Error = error, Exception = ex };
}
