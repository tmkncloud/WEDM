using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

public sealed class TransformationProgressEventArgs : EventArgs
{
    public required TransformationStageResult Stage { get; init; }
    public double OverallPercent { get; init; }
}

/// <summary>Staged migration preparation pipeline — generates artifacts only; never mutates source environments.</summary>
public interface ITransformationOrchestrator
{
    event EventHandler<TransformationProgressEventArgs>? ProgressChanged;

    Task<TransformationExecutionResult> ExecuteAsync(
        MigrationConfiguration configuration,
        TransformationExecutionOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface ITransformationValidationEngine
{
    TransformationValidationSummary Validate(
        MigrationConfiguration configuration,
        TransformationExecutionResult result);
}

public interface IMigrationPlanGenerator
{
    MigrationPlanDocument Generate(MigrationConfiguration configuration, TransformationExecutionResult result);
}
