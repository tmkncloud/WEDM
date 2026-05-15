using System.Diagnostics;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Migration;

/// <summary>Simulated enterprise discovery — produces realistic topology and metadata for assessment workflows.</summary>
public sealed class StubMiddlewareDiscoveryService : IMiddlewareDiscoveryService, IFormsEnvironmentScanner, IWebLogicTopologyAnalyzer
{
    public async Task<MiddlewareTopologySnapshot> DiscoverTopologyAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken = default)
    {
        await SimulateWorkAsync(cancellationToken);
        var (topology, _, _) = EnterpriseDiscoverySimulator.Build(NormalizeSource(source));
        return topology;
    }

    public async Task<MiddlewareTopologySnapshot> AnalyzeDomainAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken = default)
    {
        await SimulateWorkAsync(cancellationToken);
        var (topology, _, _) = EnterpriseDiscoverySimulator.Build(NormalizeSource(source));
        return topology;
    }

    public async Task<FormsReportsMetadataSnapshot> ScanFormsEnvironmentAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken = default)
    {
        await SimulateWorkAsync(cancellationToken);
        var (_, forms, _) = EnterpriseDiscoverySimulator.Build(NormalizeSource(source));
        return forms;
    }

    /// <summary>Full discovery pass — topology, Forms metadata, and insight register.</summary>
    public async Task<(MiddlewareTopologySnapshot Topology, FormsReportsMetadataSnapshot Forms, List<EnvironmentDiscoveryFinding> Insights)>
        DiscoverFullAsync(MigrationEnvironmentProfile source, CancellationToken cancellationToken = default)
    {
        await SimulateWorkAsync(cancellationToken);
        return EnterpriseDiscoverySimulator.Build(NormalizeSource(source));
    }

    private static MigrationEnvironmentProfile NormalizeSource(MigrationEnvironmentProfile source)
    {
        source.HostName       ??= Environment.MachineName;
        source.MiddlewareHome ??= @"D:\Oracle\Middleware";
        source.DomainHome     ??= @"D:\Oracle\user_projects\domains\base_domain";
        source.FormsHome      ??= Path.Combine(source.MiddlewareHome, "forms");
        source.ReportsHome    ??= Path.Combine(source.MiddlewareHome, "reports");
        return source;
    }

    private static async Task SimulateWorkAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(750, cancellationToken);
    }
}

