using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Silent RCU execution with response-file generation and schema safety checks.</summary>
public interface IRcuAutomationService
{
    Task<RcuPrecheckResult> PrecheckAsync(DeploymentConfiguration config, CancellationToken cancellationToken = default);

    Task<RcuExecutionResult> ExecuteAsync(
        DeploymentConfiguration config,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed class RcuPrecheckResult
{
    public bool CanProceed { get; init; }
    public bool SchemasExist { get; init; }
    public bool CharsetValid { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = [];
    public IReadOnlyList<string> ExistingSchemas { get; init; } = [];
}

public sealed class RcuExecutionResult
{
    public bool Success { get; init; }
    public bool Skipped { get; init; }
    public bool DryRun { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public string? ResponseFilePath { get; init; }
}
