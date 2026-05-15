using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

public sealed class MigrationExecutionProgressEventArgs : EventArgs
{
    public required MigrationExecutionStageResult Stage { get; init; }
    public double OverallPercent { get; init; }
    public string? LogLine { get; init; }
}

public sealed class MigrationExecutionCheckpointEventArgs : EventArgs
{
    public required ExecutionCheckpointRecord Checkpoint { get; init; }
}

public interface IMigrationExecutionOrchestrator
{
    event EventHandler<MigrationExecutionProgressEventArgs>? ProgressChanged;
    event EventHandler<MigrationExecutionCheckpointEventArgs>? CheckpointRequired;

    Task<MigrationExecutionResult> ExecuteAsync(
        MigrationConfiguration configuration,
        MigrationExecutionOptions options,
        CancellationToken cancellationToken = default);

    void SubmitCheckpointDecision(CheckpointDecision decision);
    void CancelActiveExecution();
}

public interface IMigrationPreflightValidator
{
    PreflightValidationResult Validate(MigrationConfiguration configuration, MigrationExecutionOptions options);
}

public interface IWlstExecutionService
{
    Task<WlstExecutionRecord> ExecuteScriptAsync(
        string wlstCmd,
        string scriptPath,
        MigrationExecutionCredentials? credentials,
        bool dryRun,
        string logDirectory,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null);
}

public interface IMigrationExecutionValidationEngine
{
    ExecutionValidationSummary ValidatePostExecution(MigrationConfiguration configuration, MigrationExecutionResult result);
    ExecutionValidationSummary ValidateStage(MigrationConfiguration configuration, MigrationExecutionStageKind stage, MigrationExecutionResult result);
}

public interface IMigrationExecutionStateStore
{
    Task SaveAsync(string workspacePath, MigrationExecutionResult result, CancellationToken cancellationToken = default);
    Task<MigrationExecutionResult?> LoadAsync(string workspacePath, CancellationToken cancellationToken = default);
}

public interface IMigrationExecutionReportWriter
{
    Task<string> WriteJsonAsync(MigrationConfiguration configuration, MigrationExecutionResult result, string outputDirectory, CancellationToken cancellationToken = default);
    Task<string> WriteHtmlAsync(MigrationConfiguration configuration, MigrationExecutionResult result, string outputDirectory, CancellationToken cancellationToken = default);
}
