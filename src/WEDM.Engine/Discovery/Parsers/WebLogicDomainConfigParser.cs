using System.Xml.Linq;
using WEDM.Domain.Models;

namespace WEDM.Engine.Discovery.Parsers;

/// <summary>Read-only parser for WebLogic domain config.xml with surfaced warnings.</summary>
public static class WebLogicDomainConfigParser
{
    private static readonly XNamespace DomainNs = "http://xmlns.oracle.com/weblogic/domain";

    public static DomainAnalysisSnapshot Parse(string domainHome, string? configXmlPath = null)
    {
        var result = new DomainAnalysisSnapshot { DomainHome = domainHome };
        configXmlPath ??= Path.Combine(domainHome, "config", "config.xml");
        if (!File.Exists(configXmlPath))
        {
            AddWarning(result, $"config.xml not found: {configXmlPath}");
            return result;
        }

        try
        {
            var doc = XDocument.Load(configXmlPath, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            if (root is null)
            {
                AddWarning(result, "config.xml root element is missing.");
                return result;
            }

            result.DomainName      = ElementValue(root, "name");
            result.AdminServerName = ElementValue(root, "admin-server-name");
            result.ProductionMode  = bool.TryParse(ElementValue(root, "production-mode-enabled"), out var prod) && prod;
            result.BootPropertiesPresent = File.Exists(Path.Combine(domainHome, "servers", result.AdminServerName ?? "AdminServer", "security", "boot.properties"))
                || File.Exists(Path.Combine(domainHome, "security", "boot.properties"));

            var servers = root.Elements(DomainNs + "server").Concat(root.Elements("server")).ToList();
            result.MachineCount = root.Elements(DomainNs + "machine").Concat(root.Elements("machine")).Count();
            result.JdbcResourceCount = root.Elements(DomainNs + "jdbc-system-resource").Concat(root.Elements("jdbc-system-resource")).Count();
            result.DeploymentTargetCount = root.Elements(DomainNs + "app-deployment").Concat(root.Elements("app-deployment")).Count();

            var adminName = result.AdminServerName ?? "AdminServer";
            foreach (var server in servers)
            {
                var name = ElementValue(server, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (name.Equals(adminName, StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(ElementValue(server, "listen-port"), out var port))
                        result.AdminListenPort = port;
                    continue;
                }

                int.TryParse(ElementValue(server, "listen-port"), out _);
                result.StartupScriptPaths.Add(Path.Combine(domainHome, "servers", name, "bin", "setStartupEnv.cmd"));
            }
        }
        catch (Exception ex)
        {
            AddWarning(result, $"Failed to parse config.xml: {ex.Message}");
        }

        return result;
    }

    public static List<ManagedServerDescriptor> ParseManagedServers(string domainHome, string? adminServerName, DomainAnalysisSnapshot? analysis = null)
    {
        var list = new List<ManagedServerDescriptor>();
        var configPath = Path.Combine(domainHome, "config", "config.xml");
        if (!File.Exists(configPath))
        {
            if (analysis is not null)
                AddWarning(analysis, $"config.xml not found for managed server parse: {configPath}");
            return list;
        }

        try
        {
            var doc = XDocument.Load(configPath);
            var root = doc.Root;
            if (root is null)
            {
                if (analysis is not null)
                    AddWarning(analysis, "Managed server parse: config.xml has no root element.");
                return list;
            }

            adminServerName ??= ElementValue(root, "admin-server-name") ?? "AdminServer";

            foreach (var server in root.Elements(DomainNs + "server").Concat(root.Elements("server")))
            {
                var name = ElementValue(server, "name");
                if (string.IsNullOrWhiteSpace(name) || name.Equals(adminServerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                int.TryParse(ElementValue(server, "listen-port"), out var port);
                list.Add(new ManagedServerDescriptor
                {
                    Name       = name,
                    Cluster    = ElementValue(server, "cluster"),
                    ListenPort = port > 0 ? port : 0,
                    State      = "UNKNOWN",
                });
            }
        }
        catch (Exception ex)
        {
            if (analysis is not null)
                AddWarning(analysis, $"Managed server parse failed: {ex.Message}");
        }

        return list;
    }

    public static List<ClusterDescriptor> ParseClusters(string domainHome, DomainAnalysisSnapshot? analysis = null)
    {
        var list = new List<ClusterDescriptor>();
        var configPath = Path.Combine(domainHome, "config", "config.xml");
        if (!File.Exists(configPath))
        {
            if (analysis is not null)
                AddWarning(analysis, $"config.xml not found for cluster parse: {configPath}");
            return list;
        }

        try
        {
            var doc = XDocument.Load(configPath);
            var root = doc.Root;
            if (root is null)
            {
                if (analysis is not null)
                    AddWarning(analysis, "Cluster parse: config.xml has no root element.");
                return list;
            }

            foreach (var cluster in root.Elements(DomainNs + "cluster").Concat(root.Elements("cluster")))
            {
                var name = ElementValue(cluster, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var members = root.Elements(DomainNs + "server").Concat(root.Elements("server"))
                    .Count(s => string.Equals(ElementValue(s, "cluster"), name, StringComparison.OrdinalIgnoreCase));
                list.Add(new ClusterDescriptor { Name = name, MemberCount = members });
            }
        }
        catch (Exception ex)
        {
            if (analysis is not null)
                AddWarning(analysis, $"Cluster parse failed: {ex.Message}");
        }

        return list;
    }

    private static void AddWarning(DomainAnalysisSnapshot snapshot, string message)
    {
        snapshot.ParseWarnings.Add(message);
        snapshot.ParseHealthy = false;
    }

    private static string? ElementValue(XElement parent, string localName)
    {
        var el = parent.Element(DomainNs + localName) ?? parent.Element(localName);
        return el?.Value?.Trim();
    }
}
