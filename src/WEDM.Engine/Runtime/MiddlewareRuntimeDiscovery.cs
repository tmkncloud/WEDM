using System.Xml.Linq;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Runtime;

/// <summary>
/// Discovers WebLogic domain topology from the filesystem.
///
/// Discovery strategy (in priority order):
///   1. config.xml at {domainHome}/config/config.xml — authoritative server topology.
///   2. {domainHome}/nodemanager/ — presence confirms NodeManager is configured.
///   3. {middlewareHome}/ohs/ or {oracleHome}/ohs/ — presence confirms OHS.
///   4. Running process table via <see cref="IOracleProcessLifecycleService"/> —
///      enriches topology with live PID/port data.
///
/// All parse failures are captured as warnings in <see cref="DomainRuntimeTopology.Warnings"/>
/// — discovery never throws an unhandled exception.
/// </summary>
public sealed class MiddlewareRuntimeDiscovery
{
    private static readonly XNamespace DomainNs = "http://xmlns.oracle.com/weblogic/domain";
    private const int DefaultAdminPort      = 7001;
    private const int DefaultNodeManagerPort = 5556;

    private readonly ILoggingService              _log;
    private readonly IOracleProcessLifecycleService _lifecycle;

    public MiddlewareRuntimeDiscovery(
        ILoggingService               log,
        IOracleProcessLifecycleService lifecycle)
    {
        _log       = log;
        _lifecycle = lifecycle;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Discovers all WebLogic domains under the given middleware home.
    /// When <paramref name="middlewareHome"/> is null, searches under ProgramData/WEDM.
    /// </summary>
    public Task<IReadOnlyList<DomainRuntimeTopology>> DiscoverDomainsAsync(
        string?           middlewareHome,
        CancellationToken ct = default)
    {
        var results = new List<DomainRuntimeTopology>();

        var searchRoots = BuildSearchRoots(middlewareHome);
        foreach (var root in searchRoots)
        {
            try
            {
                DiscoverUnder(root, results);
            }
            catch (Exception ex)
            {
                _log.Warning($"[RuntimeDiscovery] Error searching {root}: {ex.Message}", "Runtime");
            }
        }

        // Deduplicate by DomainHome
        var deduped = results
            .GroupBy(t => t.DomainHome, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        _log.Info($"[RuntimeDiscovery] Found {deduped.Count} domain(s) in {searchRoots.Count} search root(s).", "Runtime");
        return Task.FromResult<IReadOnlyList<DomainRuntimeTopology>>(deduped.AsReadOnly());
    }

    /// <summary>
    /// Parses a specific domain home and returns its topology.
    /// Returns a topology with warnings (and empty data) when config.xml is missing.
    /// </summary>
    public DomainRuntimeTopology ParseDomainTopology(string domainHome)
    {
        var warnings = new List<string>();
        var configXml = Path.Combine(domainHome, "config", "config.xml");

        if (!File.Exists(configXml))
        {
            warnings.Add($"config.xml not found at: {configXml}");
            return new DomainRuntimeTopology
            {
                DomainHome   = domainHome,
                DomainName   = Path.GetFileName(domainHome),
                Warnings     = warnings.AsReadOnly(),
                DiscoveredAt = DateTimeOffset.UtcNow,
            };
        }

        try
        {
            return ParseConfigXml(domainHome, configXml, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to parse config.xml: {ex.Message}");
            _log.Warning($"[RuntimeDiscovery] config.xml parse error in {domainHome}: {ex.Message}", "Runtime");
            return new DomainRuntimeTopology
            {
                DomainHome   = domainHome,
                DomainName   = Path.GetFileName(domainHome),
                Warnings     = warnings.AsReadOnly(),
                DiscoveredAt = DateTimeOffset.UtcNow,
            };
        }
    }

    /// <summary>
    /// Builds <see cref="RuntimeComponent"/> list from a topology, pre-populated with
    /// path and port data.  Process state is left as <see cref="RuntimeState.Unknown"/>
    /// — the health-check service will populate live state.
    /// </summary>
    public IReadOnlyList<RuntimeComponent> BuildComponents(DomainRuntimeTopology topology)
    {
        var components = new List<RuntimeComponent>();

        // AdminServer (always present)
        components.Add(new RuntimeComponent
        {
            Name        = topology.AdminServerName,
            Kind        = ComponentKind.AdminServer,
            DomainName  = topology.DomainName,
            DomainHome  = topology.DomainHome,
            Host        = topology.AdminHost,
            Port        = topology.AdminPort,
            LogFile     = AdminServerLogPath(topology),
            StartScript = AdminServerStartScript(topology),
        });

        // Managed servers
        foreach (var ms in topology.ManagedServers)
        {
            components.Add(new RuntimeComponent
            {
                Name        = ms.Name,
                Kind        = ComponentKind.ManagedServer,
                DomainName  = topology.DomainName,
                DomainHome  = topology.DomainHome,
                Host        = ms.Host,
                Port        = ms.Port,
                LogFile     = Path.Combine(topology.DomainHome, "servers", ms.Name, "logs", $"{ms.Name}.log"),
                StartScript = string.Empty,   // managed servers require NM or manual start
            });
        }

        // NodeManager
        if (topology.HasNodeManager)
        {
            components.Add(new RuntimeComponent
            {
                Name        = "NodeManager",
                Kind        = ComponentKind.NodeManager,
                DomainName  = topology.DomainName,
                DomainHome  = topology.DomainHome,
                Host        = topology.AdminHost,
                Port        = topology.NodeManagerPort,
                LogFile     = NodeManagerLogPath(topology),
                StartScript = NodeManagerStartScript(topology),
            });
        }

        // OHS
        if (topology.HasOHS)
        {
            components.Add(new RuntimeComponent
            {
                Name        = "OHS",
                Kind        = ComponentKind.OHS,
                DomainName  = topology.DomainName,
                DomainHome  = topology.DomainHome,
                Host        = topology.AdminHost,
                Port        = topology.OhsPort > 0 ? topology.OhsPort : 7777,
                LogFile     = OhsLogPath(topology),
                StartScript = string.Empty,
            });
        }

        return components.AsReadOnly();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers — config.xml parsing
    // ─────────────────────────────────────────────────────────────────────────

    private DomainRuntimeTopology ParseConfigXml(
        string domainHome, string configXml, List<string> warnings)
    {
        var doc  = XDocument.Load(configXml, LoadOptions.PreserveWhitespace);
        var root = doc.Root;
        if (root is null)
        {
            warnings.Add("config.xml root element is null.");
            return EmptyTopology(domainHome, warnings);
        }

        var domainName   = ReadElement(root, "name") ?? Path.GetFileName(domainHome);
        var adminSrvName = ReadElement(root, "admin-server-name") ?? "AdminServer";

        // Read server elements (with and without namespace)
        var servers = root.Elements(DomainNs + "server")
            .Concat(root.Elements("server"))
            .ToList();

        string adminHost = "localhost";
        int    adminPort = DefaultAdminPort;

        var managedServers = new List<ManagedServerEntry>();

        foreach (var srv in servers)
        {
            var srvName = ReadElement(srv, "name");
            if (string.IsNullOrWhiteSpace(srvName)) continue;

            var rawPort    = ReadElement(srv, "listen-port");
            var rawAddr    = ReadElement(srv, "listen-address");
            int parsedPort = int.TryParse(rawPort, out var p) ? p : 0;
            var host       = string.IsNullOrWhiteSpace(rawAddr) ? "localhost" : rawAddr;

            if (srvName.Equals(adminSrvName, StringComparison.OrdinalIgnoreCase))
            {
                adminPort = parsedPort > 0 ? parsedPort : DefaultAdminPort;
                adminHost = host;
            }
            else
            {
                managedServers.Add(new ManagedServerEntry
                {
                    Name = srvName,
                    Host = host,
                    Port = parsedPort > 0 ? parsedPort : 7002,
                });
            }
        }

        // NodeManager detection: check nodemanager/ directory
        var nmHome = Path.Combine(domainHome, "nodemanager");
        var hasNm  = Directory.Exists(nmHome);
        int nmPort = DefaultNodeManagerPort;

        if (hasNm)
        {
            // Try to read nodemanager.properties for the port
            var nmProps = Path.Combine(nmHome, "nodemanager.properties");
            if (File.Exists(nmProps))
                nmPort = ReadNodeManagerPort(nmProps) ?? DefaultNodeManagerPort;
        }

        // OHS detection: check for OHS domain directory
        var ohsHome = DetectOhsHome(domainHome);
        int ohsPort = ohsHome is not null ? DetectOhsPort(ohsHome) : 0;

        // WebLogic version from product registry or manifest
        var wlsVersion = DetectWebLogicVersion(domainHome);

        return new DomainRuntimeTopology
        {
            DomainName      = domainName,
            DomainHome      = domainHome,
            AdminServerName = adminSrvName,
            AdminHost       = adminHost,
            AdminPort       = adminPort,
            ManagedServers  = managedServers.AsReadOnly(),
            HasNodeManager  = hasNm,
            NodeManagerHome = hasNm ? nmHome : null,
            NodeManagerPort = nmPort,
            HasOHS          = ohsHome is not null,
            OhsHome         = ohsHome,
            OhsPort         = ohsPort,
            WebLogicVersion = wlsVersion,
            Warnings        = warnings.AsReadOnly(),
            DiscoveredAt    = DateTimeOffset.UtcNow,
        };
    }

    private static string? ReadElement(XElement parent, string localName)
    {
        var el = parent.Element(DomainNs + localName) ?? parent.Element(localName);
        return el?.Value?.Trim();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Filesystem search helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static List<string> BuildSearchRoots(string? middlewareHome)
    {
        var roots = new List<string>();

        if (!string.IsNullOrWhiteSpace(middlewareHome) && Directory.Exists(middlewareHome))
        {
            // Search for domains/ under the middleware home
            var domainsDir = Path.Combine(middlewareHome, "user_projects", "domains");
            if (Directory.Exists(domainsDir)) roots.Add(domainsDir);

            var altDomains = Path.Combine(middlewareHome, "domains");
            if (Directory.Exists(altDomains)) roots.Add(altDomains);

            // Also try common Windows Oracle paths: C:\Oracle\user_projects\domains
            roots.Add(middlewareHome);
        }

        // Common Windows default locations
        foreach (var drive in new[] { @"C:\", @"D:\", @"E:\" })
        {
            AddIfExists(roots, Path.Combine(drive, "Oracle", "user_projects", "domains"));
            AddIfExists(roots, Path.Combine(drive, "Oracle", "Middleware", "user_projects", "domains"));
            AddIfExists(roots, Path.Combine(drive, "Oracle", "Middleware", "Oracle_Home", "user_projects", "domains"));
        }

        // WEDM-managed domains from ProgramData
        var wedmData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WEDM");
        AddIfExists(roots, wedmData);

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddIfExists(List<string> list, string path)
    {
        if (Directory.Exists(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
            list.Add(path);
    }

    private void DiscoverUnder(string root, List<DomainRuntimeTopology> results)
    {
        // A directory is a domain home if it contains config/config.xml
        if (File.Exists(Path.Combine(root, "config", "config.xml")))
        {
            results.Add(ParseDomainTopology(root));
            return;
        }

        // Recurse up to 2 levels
        try
        {
            foreach (var sub in Directory.GetDirectories(root))
            {
                try
                {
                    if (File.Exists(Path.Combine(sub, "config", "config.xml")))
                        results.Add(ParseDomainTopology(sub));
                }
                catch (Exception ex)
                {
                    _log.Verbose($"[RuntimeDiscovery] Skip {sub}: {ex.Message}", "Runtime");
                }
            }
        }
        catch (UnauthorizedAccessException) { /* skip inaccessible dirs */ }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NodeManager helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static int? ReadNodeManagerPort(string propsFile)
    {
        try
        {
            foreach (var line in File.ReadLines(propsFile))
            {
                if (line.StartsWith("ListenPort=", StringComparison.OrdinalIgnoreCase))
                {
                    var val = line["ListenPort=".Length..].Trim();
                    if (int.TryParse(val, out var p)) return p;
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OHS helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string? DetectOhsHome(string domainHome)
    {
        // Check for OHS config directory inside domain
        var ohsDomainCfg = Path.Combine(domainHome, "config", "fmwconfig", "components", "OHS");
        if (Directory.Exists(ohsDomainCfg)) return ohsDomainCfg;

        // Check sibling directory
        var parent = Path.GetDirectoryName(domainHome);
        if (parent is null) return null;

        foreach (var candidate in new[] { "ohs", "OHS", "webtier", "WebTier" })
        {
            var path = Path.Combine(parent, candidate);
            if (Directory.Exists(path)) return path;
        }

        return null;
    }

    private static int DetectOhsPort(string ohsHome)
    {
        // Try to read httpd.conf or ssl.conf for the Listen port
        try
        {
            foreach (var candidate in Directory.GetFiles(ohsHome, "httpd.conf", SearchOption.AllDirectories).Take(1))
            {
                foreach (var line in File.ReadLines(candidate))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Listen ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && int.TryParse(parts[1].Split(':').Last(), out var p))
                            return p;
                    }
                }
            }
        }
        catch { /* ignore */ }
        return 7777; // OHS default
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WLS version detection
    // ─────────────────────────────────────────────────────────────────────────

    private static string? DetectWebLogicVersion(string domainHome)
    {
        // Probe the registry file from the WLS installation
        try
        {
            // Check wlserver/server/lib/weblogic.jar manifest (not worth cracking the zip here)
            // Instead look for version in the domain's config or in the MW_HOME
            var wlParent = FindWebLogicHome(domainHome);
            if (wlParent is null) return null;

            var versionFile = Path.Combine(wlParent, "server", "lib", "version.properties");
            if (File.Exists(versionFile))
            {
                foreach (var line in File.ReadLines(versionFile))
                {
                    if (line.StartsWith("weblogic.version=", StringComparison.OrdinalIgnoreCase))
                        return line["weblogic.version=".Length..].Trim();
                }
            }

            // Fallback: check for known directory names
            var dirName = Path.GetFileName(wlParent) ?? string.Empty;
            if (dirName.Contains("12.2", StringComparison.Ordinal)) return "12.2.x";
            if (dirName.Contains("14.1", StringComparison.Ordinal)) return "14.1.x";
        }
        catch { /* ignore */ }
        return null;
    }

    private static string? FindWebLogicHome(string domainHome)
    {
        // Walk up looking for wlserver/ sibling
        var dir = domainHome;
        for (var i = 0; i < 5; i++)
        {
            dir = Path.GetDirectoryName(dir);
            if (dir is null) break;
            foreach (var candidate in new[] { "wlserver", "wlserver_12.1", "wlserver_10.3" })
            {
                var wls = Path.Combine(dir, candidate);
                if (Directory.Exists(wls)) return wls;
            }
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Path helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string AdminServerLogPath(DomainRuntimeTopology t)
        => Path.Combine(t.DomainHome, "servers", t.AdminServerName, "logs", $"{t.AdminServerName}.log");

    private static string AdminServerStartScript(DomainRuntimeTopology t)
        => Path.Combine(t.DomainHome, "bin", "startWebLogic.cmd");

    private static string NodeManagerLogPath(DomainRuntimeTopology t)
        => t.NodeManagerHome is not null
            ? Path.Combine(t.NodeManagerHome, "nodemanager.log")
            : Path.Combine(t.DomainHome, "nodemanager", "nodemanager.log");

    private static string NodeManagerStartScript(DomainRuntimeTopology t)
        => Path.Combine(t.DomainHome, "bin", "startNodeManager.cmd");

    private static string OhsLogPath(DomainRuntimeTopology t)
        => t.OhsHome is not null
            ? Path.Combine(t.OhsHome, "logs", "access_log")
            : string.Empty;

    private static DomainRuntimeTopology EmptyTopology(string domainHome, List<string> warnings)
        => new()
        {
            DomainHome   = domainHome,
            DomainName   = Path.GetFileName(domainHome),
            Warnings     = warnings.AsReadOnly(),
            DiscoveredAt = DateTimeOffset.UtcNow,
        };
}
