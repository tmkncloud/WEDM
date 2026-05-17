using System.Diagnostics;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;
using WEDM.Infrastructure.Registry;

namespace WEDM.Engine.Decommissioning;

public sealed class EnvironmentDiscoveryService : IEnvironmentDiscoveryService
{
    private readonly IOracleInventoryAnalyzer _inventory;
    private readonly IOracleProcessManager   _processes;
    private readonly WindowsRegistryService  _registry;

    public EnvironmentDiscoveryService(
        IOracleInventoryAnalyzer inventory,
        IOracleProcessManager processes,
        WindowsRegistryService registry)
    {
        _inventory = inventory;
        _processes = processes;
        _registry  = registry;
    }

    public Task<EnvironmentTopology> DiscoverAsync(
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var topology = new EnvironmentTopology
        {
            DiscoveredAt = DateTimeOffset.UtcNow,
            MachineName  = Environment.MachineName,
        };

        DiscoverInventory(config, topology);
        DiscoverHomes(config, topology);
        DiscoverDomains(config, topology);
        DiscoverServices(topology);
        topology.Processes.AddRange(_processes.DetectMiddlewareProcesses());
        DiscoverPorts(topology);
        DiscoverJdks(config, topology);
        DiscoverTempArtifacts(config, topology);

        return Task.FromResult(topology);
    }

    private void DiscoverHomes(DecommissionConfiguration config, EnvironmentTopology topology)
    {
        if (Directory.Exists(config.Paths.MiddlewareHome))
        {
            topology.MiddlewareHomes.Add(new OracleHomeDescriptor
            {
                Path = config.Paths.MiddlewareHome,
                Name = "Configured Middleware Home",
            });
        }

        foreach (var home in topology.InventoryHomes.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (topology.MiddlewareHomes.Any(h => h.Path.Equals(home, StringComparison.OrdinalIgnoreCase)))
                continue;
            topology.MiddlewareHomes.Add(new OracleHomeDescriptor { Path = home, Name = "Inventory Home" });
        }
    }

    private static void DiscoverDomains(DecommissionConfiguration config, EnvironmentTopology topology)
    {
        if (!Directory.Exists(config.Paths.DomainBase))
            return;

        foreach (var dir in Directory.GetDirectories(config.Paths.DomainBase))
        {
            var configXml = Path.Combine(dir, "config", "config.xml");
            if (File.Exists(configXml))
                topology.DomainHomes.Add(dir);
        }
    }

    private static void DiscoverServices(EnvironmentTopology topology)
    {
        try
        {
            foreach (var svc in ServiceController.GetServices())
            {
                var name = svc.ServiceName ?? string.Empty;
                var display = svc.DisplayName ?? string.Empty;
                var oracleRelated = IsOracleServiceName(name) || IsOracleServiceName(display);
                if (!oracleRelated) continue;

                topology.WindowsServices.Add(new OracleWindowsServiceDescriptor
                {
                    ServiceName     = name,
                    DisplayName     = display,
                    Status          = svc.Status.ToString(),
                    IsOracleRelated = true,
                });
            }
        }
        catch (Exception ex)
        {
            topology.OrphanWarnings.Add($"Service enumeration failed: {ex.Message}");
        }
    }

    private static bool IsOracleServiceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.ToLowerInvariant();
        return v.Contains("weblogic") || v.Contains("oracle") || v.Contains("nodemanager")
            || v.Contains("forms") || v.Contains("reports") || v.Contains("ohs");
    }

    private static void DiscoverPorts(EnvironmentTopology topology)
    {
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            var listeners = props.GetActiveTcpListeners()
                .Select(e => e.Port)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
            topology.ListeningPorts.AddRange(listeners);
        }
        catch (Exception ex)
        {
            topology.OrphanWarnings.Add($"Port discovery failed: {ex.Message}");
        }
    }

    private void DiscoverJdks(DecommissionConfiguration config, EnvironmentTopology topology)
    {
        if (!string.IsNullOrWhiteSpace(config.Paths.JavaHome) && Directory.Exists(config.Paths.JavaHome))
            topology.JdkInstallations.Add(config.Paths.JavaHome);

        var regJdk = _registry.DetectInstalledJdk();
        if (!string.IsNullOrWhiteSpace(regJdk) && !topology.JdkInstallations.Contains(regJdk, StringComparer.OrdinalIgnoreCase))
            topology.JdkInstallations.Add(regJdk);

        var programFilesJava = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java");
        if (Directory.Exists(programFilesJava))
        {
            foreach (var jdk in Directory.GetDirectories(programFilesJava, "jdk*"))
                if (!topology.JdkInstallations.Contains(jdk, StringComparer.OrdinalIgnoreCase))
                    topology.JdkInstallations.Add(jdk);
        }
    }

    private static void DiscoverTempArtifacts(DecommissionConfiguration config, EnvironmentTopology topology)
    {
        var roots = new[] { config.Paths.TempDirectory, Path.GetTempPath(), config.Paths.MiddlewareHome };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root, "OraInstall*", SearchOption.TopDirectoryOnly))
                    topology.TempExtractionFolders.Add(dir);
                foreach (var dir in Directory.GetDirectories(root, "jdk*", SearchOption.TopDirectoryOnly))
                    if (dir.Contains("install", StringComparison.OrdinalIgnoreCase))
                        topology.TempExtractionFolders.Add(dir);
            }
            catch
            {
                // partial/corrupt temp — still report root
                topology.OrphanWarnings.Add($"Could not fully scan temp artifacts under {root}");
            }
        }
    }

    private void DiscoverInventory(DecommissionConfiguration config, EnvironmentTopology topology)
    {
        var inventoryRoot = config.Paths.OracleInventory;
        if (string.IsNullOrWhiteSpace(inventoryRoot))
            inventoryRoot = OracleInventoryXmlParser.ResolveInventoryLocFromOraInst(config.Paths.MiddlewareHome);

        if (string.IsNullOrWhiteSpace(inventoryRoot))
        {
            topology.OrphanWarnings.Add("Central Oracle inventory location could not be resolved.");
            return;
        }

        var analysis = _inventory.Analyze(inventoryRoot, config.Paths.MiddlewareHome);
        topology.CentralInventory = new OracleInventorySnapshot
        {
            InventoryLoc      = analysis.InventoryRoot,
            InventoryHealthy  = analysis.XmlValid && !analysis.LockPresent,
            InventoryWarning  = string.Join("; ", analysis.CorruptionWarnings),
            OracleHomes       = analysis.Homes.Select(h => new OracleHomeDescriptor { Path = h.Path, Name = h.Name }).ToList(),
        };
        topology.InventoryHomes.AddRange(analysis.Homes);
    }
}
