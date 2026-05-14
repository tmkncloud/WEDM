using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Core deployment engine contract.
/// Implementations drive the full WebLogic installation and configuration lifecycle.
/// </summary>
public interface IDeploymentEngine
{
    /// <summary>Raised whenever a step's status changes.</summary>
    event EventHandler<StepProgressEventArgs>? StepProgressChanged;

    /// <summary>Raised whenever a log line is emitted during execution.</summary>
    event EventHandler<LogLineEventArgs>? LogLineEmitted;

    /// <summary>Raised when overall deployment progress changes (0–100).</summary>
    event EventHandler<double>? OverallProgressChanged;

    /// <summary>
    /// Execute a full deployment from the given configuration.
    /// </summary>
    Task<DeploymentReport> ExecuteAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate prerequisites only — no installation performed.
    /// </summary>
    Task<PrerequisiteValidationResult> ValidateAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Roll back a previous deployment to its snapshot state.
    /// </summary>
    Task<bool> RollbackAsync(
        Guid deploymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume a failed deployment from the last successful step.
    /// </summary>
    Task<DeploymentReport> ResumeAsync(
        Guid deploymentId,
        CancellationToken cancellationToken = default);
}

public sealed class StepProgressEventArgs(DeploymentStep step) : EventArgs
{
    public DeploymentStep Step { get; } = step;
}

public sealed class LogLineEventArgs(string line, Enums.LogLevel level, string? category = null) : EventArgs
{
    public string Line     { get; } = line;
    public Enums.LogLevel Level { get; } = level;
    public string Category { get; } = category ?? "General";
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
}
