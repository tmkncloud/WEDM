using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Discovery;

/// <summary>
/// Real read-only discovery service — implements domain scanner interfaces and orchestrator entry point.
/// </summary>
public sealed class MiddlewareDiscoveryService :
    IMiddlewareDiscoveryService,
    IFormsEnvironmentScanner,
    IWebLogicTopologyAnalyzer,
    IMiddlewareDiscoveryOrchestrator
{
    private readonly MiddlewareDiscoveryOrchestrator _orchestrator;
    private DiscoveryExecutionResult? _lastResult;

    public MiddlewareDiscoveryService(MiddlewareDiscoveryOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public event EventHandler<DiscoveryProgressEventArgs>? ProgressChanged
    {
        add => _orchestrator.ProgressChanged += value;
        remove => _orchestrator.ProgressChanged -= value;
    }

    public Task<DiscoveryExecutionResult> ExecuteAsync(
        MigrationEnvironmentProfile source,
        DiscoveryScanOptions options,
        CancellationToken cancellationToken = default)
    {
        return ExecuteInternalAsync(source, options, cancellationToken);
    }

    public async Task<DiscoveryExecutionResult> DiscoverFullAsync(
        MigrationEnvironmentProfile source,
        DiscoveryScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DiscoveryScanOptions
        {
            MiddlewareHome         = source.MiddlewareHome,
            DomainHome             = source.DomainHome,
            AllowSimulatedFallback = false,
        };
        return await ExecuteInternalAsync(source, options, cancellationToken);
    }

    public Task<MiddlewareTopologySnapshot> DiscoverTopologyAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken = default)
        => ExecuteAndReturnTopologyAsync(source, cancellationToken);

    public Task<MiddlewareTopologySnapshot> AnalyzeDomainAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken = default)
        => ExecuteAndReturnTopologyAsync(source, cancellationToken);

    public Task<FormsReportsMetadataSnapshot> ScanFormsEnvironmentAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken = default)
        => ExecuteAndReturnFormsAsync(source, cancellationToken);

    private async Task<DiscoveryExecutionResult> ExecuteInternalAsync(
        MigrationEnvironmentProfile source,
        DiscoveryScanOptions options,
        CancellationToken cancellationToken)
    {
        _lastResult = await _orchestrator.ExecuteAsync(source, options, cancellationToken);
        return _lastResult;
    }

    private async Task<MiddlewareTopologySnapshot> ExecuteAndReturnTopologyAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken)
    {
        var result = await DiscoverFullAsync(source, cancellationToken: cancellationToken);
        return result.Topology;
    }

    private async Task<FormsReportsMetadataSnapshot> ExecuteAndReturnFormsAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken)
    {
        var result = await DiscoverFullAsync(source, cancellationToken: cancellationToken);
        return result.FormsMetadata;
    }
}
