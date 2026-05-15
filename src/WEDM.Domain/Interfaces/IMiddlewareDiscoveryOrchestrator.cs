using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Progress notification for discovery pipeline stages.</summary>
public sealed class DiscoveryProgressEventArgs : EventArgs
{
    public required DiscoveryStageResult Stage { get; init; }
    public double OverallPercent { get; init; }
}

/// <summary>
/// Coordinates read-only environment discovery: inventory, domain, Forms/Reports, patches, compatibility.
/// </summary>
public interface IMiddlewareDiscoveryOrchestrator
{
    event EventHandler<DiscoveryProgressEventArgs>? ProgressChanged;

    Task<DiscoveryExecutionResult> ExecuteAsync(
        MigrationEnvironmentProfile source,
        DiscoveryScanOptions options,
        CancellationToken cancellationToken = default);
}
