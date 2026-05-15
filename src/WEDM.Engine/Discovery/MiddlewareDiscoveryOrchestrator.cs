using System.Diagnostics;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;
using WEDM.Engine.Discovery.Scanners;
using WEDM.Engine.Migration;
using WEDM.Engine.Opatch;

namespace WEDM.Engine.Discovery;

/// <summary>
/// Read-only discovery pipeline coordinating inventory, domain, Forms/Reports, and patch analysis.
/// </summary>
public sealed class MiddlewareDiscoveryOrchestrator : IMiddlewareDiscoveryOrchestrator
{
    private readonly PatchInventoryScanner _patchScanner;

    public event EventHandler<DiscoveryProgressEventArgs>? ProgressChanged;

    public MiddlewareDiscoveryOrchestrator(OpatchRunner opatchRunner)
    {
        _patchScanner = new PatchInventoryScanner(opatchRunner);
    }

    public async Task<DiscoveryExecutionResult> ExecuteAsync(
        MigrationEnvironmentProfile source,
        DiscoveryScanOptions options,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new DiscoveryExecutionResult { ScanStatus = DiscoveryScanStatus.InProgress };
        var stages = new List<DiscoveryStageResult>();
        var warnings = new List<string>();
        var insights = new List<EnvironmentDiscoveryFinding>();

        var middlewareHome = options.MiddlewareHome ?? source.MiddlewareHome;
        var domainHome     = options.DomainHome ?? source.DomainHome;
        var timeout        = TimeSpan.FromSeconds(Math.Clamp(options.ScanTimeoutSeconds, 30, 600));

        NormalizeProfile(source, middlewareHome, domainHome);

        var usedReal = false;
        if (SafeDiscoveryIO.DirectoryExists(middlewareHome) || SafeDiscoveryIO.DirectoryExists(domainHome))
            usedReal = true;
        else
        {
            warnings.Add("Middleware or domain paths were not accessible — discovery will use limited analysis.");
            if (options.AllowSimulatedFallback)
            {
                return BuildSimulatedFallback(source, sw, warnings);
            }
        }

        try
        {
            // Stage 1: Inventory
            var inventory = await RunStageAsync(
                DiscoveryStageKind.InventoryScan,
                "Oracle inventory scan",
                stages, 0.10, cancellationToken,
                async ct =>
                {
                    return await SafeDiscoveryIO.WithTimeoutAsync(
                        ct2 => _patchScanner.ScanAsync(middlewareHome, options.InventoryLoc, ct2),
                        timeout, ct);
                });

            // Stage 2: Middleware home
            await RunStageAsync(
                DiscoveryStageKind.MiddlewareHomeAnalysis,
                "Middleware home analysis",
                stages, 0.20, cancellationToken,
                _ =>
                {
                    if (!SafeDiscoveryIO.DirectoryExists(middlewareHome))
                        warnings.Add($"Middleware home not found: {middlewareHome}");
                    return Task.FromResult(true);
                });

            // Stage 3: Domain
            var topology = new MiddlewareTopologySnapshot();
            var domainAnalysis = new DomainAnalysisSnapshot();

            await RunStageAsync(
                DiscoveryStageKind.DomainAnalysis,
                "WebLogic domain analysis",
                stages, 0.40, cancellationToken,
                _ =>
                {
                    if (!SafeDiscoveryIO.DirectoryExists(domainHome))
                    {
                        warnings.Add($"Domain home not found: {domainHome}");
                        return Task.FromResult(true);
                    }

                    domainAnalysis = WebLogicDomainConfigParser.Parse(domainHome!);
                    NodeManagerConfigParser.ApplyNodeManagerSettings(domainAnalysis, domainHome!);

                    var adminName = domainAnalysis.AdminServerName ?? "AdminServer";
                    var servers   = WebLogicDomainConfigParser.ParseManagedServers(domainHome!, adminName);
                    var clusters  = WebLogicDomainConfigParser.ParseClusters(domainHome!);
                    var jvmArgs   = JvmStartupAnalyzer.ExtractJvmArguments(domainHome!, adminName);
                    domainAnalysis.DeprecatedJvmFlags = JvmStartupAnalyzer
                        .AnalyzeDeprecatedArgs(jvmArgs)
                        .Select(f => f.Title.Replace("Deprecated JVM flag: ", "", StringComparison.Ordinal))
                        .ToList();

                    topology = new MiddlewareTopologySnapshot
                    {
                        DomainName            = domainAnalysis.DomainName ?? Path.GetFileName(domainHome),
                        AdminServerUrl        = BuildAdminUrl(domainAnalysis),
                        ManagedServers        = servers,
                        ManagedServerCount    = servers.Count,
                        Clusters              = clusters,
                        ClusterCount          = clusters.Count,
                        NodeManagerConfigured = domainAnalysis.NodeManagerPropertiesPath is not null,
                        NodeManagerType       = domainAnalysis.NodeManagerSecure == true ? "SSL" : "Plain",
                        JvmArguments          = jvmArgs,
                        SslEnabled            = DetectSsl(domainHome!),
                        SslProtocolSummary    = DetectSsl(domainHome!) ? "SSL configuration detected in domain" : "Plain",
                        ScanStatus            = DiscoveryScanStatus.InProgress,
                        DiscoveredAtUtc       = DateTimeOffset.UtcNow,
                    };

                    return Task.FromResult(true);
                });

            // Stage 4: Forms
            FormsReportsMetadataSnapshot forms = new();
            await RunStageAsync(
                DiscoveryStageKind.FormsDiscovery,
                "Forms environment discovery",
                stages, 0.60, cancellationToken,
                _ =>
                {
                    forms = FormsMetadataScanner.Scan(source.FormsHome, middlewareHome);
                    return Task.FromResult(true);
                });

            // Stage 5: Reports
            await RunStageAsync(
                DiscoveryStageKind.ReportsDiscovery,
                "Reports environment discovery",
                stages, 0.75, cancellationToken,
                _ =>
                {
                    var (updated, reportsServers) = ReportsMetadataScanner.Scan(source.ReportsHome, middlewareHome, forms);
                    forms = updated;
                    topology.ReportsServers = reportsServers;
                    topology.OhsInstances   = Math.Max(topology.OhsInstances, reportsServers.Count);
                    return Task.FromResult(true);
                });

            // Stage 6: Patch inventory (already done — mark stage)
            await RunStageAsync(
                DiscoveryStageKind.PatchInventory,
                "Patch inventory analysis",
                stages, 0.85, cancellationToken,
                _ => Task.FromResult(true));

            // Stage 7: Compatibility evaluation
            await RunStageAsync(
                DiscoveryStageKind.CompatibilityEvaluation,
                "Compatibility evaluation",
                stages, 0.95, cancellationToken,
                _ =>
                {
                    insights.AddRange(RealEnvironmentAnalyzer.Analyze(topology, domainAnalysis, forms, inventory));
                    insights.AddRange(DiscoveryInsightBuilder.Build(topology, domainAnalysis, forms, inventory, insights));
                    return Task.FromResult(true);
                });

            topology.ScanStatus = warnings.Count > 0 ? DiscoveryScanStatus.Partial : DiscoveryScanStatus.Completed;
            topology.DiscoveredAtUtc = DateTimeOffset.UtcNow;

            result.Topology       = topology;
            result.FormsMetadata  = forms;
            result.OracleInventory = inventory;
            result.DomainAnalysis = domainAnalysis;
            result.Insights       = insights.DistinctBy(i => i.Title).ToList();
            result.Stages         = stages;
            result.Warnings       = warnings;
            result.UsedRealScan   = usedReal;
            result.ScanStatus     = topology.ScanStatus;
        }
        catch (OperationCanceledException)
        {
            result.ScanStatus = DiscoveryScanStatus.Failed;
            warnings.Add("Discovery was cancelled.");
            result.Warnings = warnings;
        }
        catch (Exception ex)
        {
            result.ScanStatus = DiscoveryScanStatus.Failed;
            warnings.Add($"Discovery error: {ex.Message}");
            result.Warnings = warnings;

            if (options.AllowSimulatedFallback)
                return BuildSimulatedFallback(source, sw, warnings);
        }

        result.TotalDurationMs = sw.ElapsedMilliseconds;
        await RunStageAsync(
            DiscoveryStageKind.ReadinessScoring,
            "Readiness data capture",
            stages, 1.0, cancellationToken,
            _ => Task.FromResult(true));

        result.Stages = stages;
        return result;
    }

    private DiscoveryExecutionResult BuildSimulatedFallback(
        MigrationEnvironmentProfile source,
        Stopwatch sw,
        List<string> warnings)
    {
        var (topology, forms, simulatedInsights) = EnterpriseDiscoverySimulator.Build(source);
        warnings.Add("Simulated discovery fallback was used because real paths were inaccessible.");
        return new DiscoveryExecutionResult
        {
            Topology        = topology,
            FormsMetadata   = forms,
            Insights        = simulatedInsights,
            Warnings        = warnings,
            UsedRealScan    = false,
            ScanStatus      = topology.ScanStatus,
            TotalDurationMs = sw.ElapsedMilliseconds,
        };
    }

    private async Task<T> RunStageAsync<T>(
        DiscoveryStageKind kind,
        string displayName,
        List<DiscoveryStageResult> stages,
        double overallPercent,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> action)
    {
        var stageSw = Stopwatch.StartNew();
        var stage = new DiscoveryStageResult
        {
            Stage       = kind,
            DisplayName = displayName,
            Status      = DiscoveryStageStatus.Running,
        };
        stages.Add(stage);
        RaiseProgress(stage, overallPercent);

        try
        {
            var value = await action(cancellationToken);
            stage.Status     = DiscoveryStageStatus.Completed;
            stage.DurationMs = stageSw.ElapsedMilliseconds;
            stage.Message    = "Completed";
            RaiseProgress(stage, overallPercent);
            return value;
        }
        catch (Exception ex)
        {
            stage.Status     = DiscoveryStageStatus.Failed;
            stage.DurationMs = stageSw.ElapsedMilliseconds;
            stage.Message    = ex.Message;
            RaiseProgress(stage, overallPercent);
            throw;
        }
    }

    private async Task RunStageAsync(
        DiscoveryStageKind kind,
        string displayName,
        List<DiscoveryStageResult> stages,
        double overallPercent,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>> action)
        => await RunStageAsync(kind, displayName, stages, overallPercent, cancellationToken,
            async ct => { await action(ct); return true; });

    private void RaiseProgress(DiscoveryStageResult stage, double percent)
    {
        ProgressChanged?.Invoke(this, new DiscoveryProgressEventArgs
        {
            Stage           = stage,
            OverallPercent  = percent * 100,
        });
    }

    private static void NormalizeProfile(MigrationEnvironmentProfile source, string? mw, string? domain)
    {
        source.MiddlewareHome ??= mw;
        source.DomainHome     ??= domain;
        source.HostName       ??= Environment.MachineName;
        if (SafeDiscoveryIO.DirectoryExists(source.MiddlewareHome))
        {
            source.FormsHome   ??= Path.Combine(source.MiddlewareHome!, "forms");
            source.ReportsHome ??= Path.Combine(source.MiddlewareHome!, "reports");
        }
    }

    private static string? BuildAdminUrl(DomainAnalysisSnapshot domain)
    {
        if (domain.AdminListenPort is null or 0) return null;
        return $"t3://localhost:{domain.AdminListenPort}";
    }

    private static bool DetectSsl(string domainHome)
    {
        var config = Path.Combine(domainHome, "config", "config.xml");
        var text = SafeDiscoveryIO.ReadAllTextSafe(config, 512_000);
        return text?.Contains("ssl", StringComparison.OrdinalIgnoreCase) == true;
    }
}
