using System.Text.Json;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>
/// Creates the deterministic directory tree required by Oracle installers and WEDM.
/// The operation is idempotent and safe to re-run after a failed deployment.
/// </summary>
public sealed class CreateOracleFoldersStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public CreateOracleFoldersStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var directories = new[]
        {
            config.Paths.OracleRoot,
            config.Paths.MiddlewareHome,
            config.Paths.DomainBase,
            config.Paths.OracleInventory,
            config.Paths.TempDirectory,
            config.Paths.LogDirectory,
            config.Paths.ReportsDirectory,
            config.Paths.SnapshotDirectory
        };

        foreach (var directory in directories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            _log.Debug($"Ensured directory exists: {directory}", "Preparation");
        }

        sw.Stop();
        return Task.FromResult(StepExecutionResult.Ok($"Prepared {directories.Length} directories.", sw.Elapsed));
    }
}

/// <summary>
/// Captures a lightweight pre-install snapshot manifest used by audit, resume, and rollback phases.
/// This intentionally records metadata only; full binary backups are a configurable advanced feature.
/// </summary>
public sealed class CreateSnapshotStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public CreateSnapshotStep(ILoggingService log) => _log = log;

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(config.Paths.SnapshotDirectory);

        var snapshot = new
        {
            config.Id,
            config.Name,
            CapturedAt = DateTimeOffset.UtcNow,
            Machine = Environment.MachineName,
            User = Environment.UserName,
            config.WebLogicVersion,
            config.Environment,
            Paths = config.Paths,
            ExistingDirectories = new
            {
                MiddlewareHome = Directory.Exists(config.Paths.MiddlewareHome),
                DomainBase = Directory.Exists(config.Paths.DomainBase),
                OracleInventory = Directory.Exists(config.Paths.OracleInventory)
            }
        };

        var path = Path.Combine(config.Paths.SnapshotDirectory, $"snapshot-{config.Id:N}.json");
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);

        _log.Info($"Snapshot manifest captured: {path}", "Preparation");
        sw.Stop();
        return StepExecutionResult.Ok(path, sw.Elapsed);
    }
}
