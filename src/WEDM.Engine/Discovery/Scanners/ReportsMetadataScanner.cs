using WEDM.Domain.Models;

namespace WEDM.Engine.Discovery.Scanners;

public static class ReportsMetadataScanner
{
    public static (FormsReportsMetadataSnapshot Metadata, List<ReportsServerDescriptor> Servers) Scan(
        string? reportsHome,
        string? middlewareHome,
        FormsReportsMetadataSnapshot existing)
    {
        var servers = new List<ReportsServerDescriptor>();
        reportsHome ??= ResolveReportsHome(middlewareHome);
        if (string.IsNullOrWhiteSpace(reportsHome) || !Directory.Exists(reportsHome))
            return (existing, servers);

        int rdf = 0, rep = 0;
        foreach (var file in SafeDiscoveryIO.EnumerateFilesSafe(reportsHome, "*.*", 5000))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".rdf" or ".xdoz") rdf++;
            if (ext is ".rep" or ".rex") rep++;
        }

        existing.ReportCount = Math.Max(existing.ReportCount, rdf + rep);

        var rwServlet = FindFile(reportsHome, "rwservlet.properties");
        var rwShowJobs = FindFile(reportsHome, "rwbuilder.conf");
        if (rwServlet is not null || rwShowJobs is not null)
        {
            servers.Add(new ReportsServerDescriptor
            {
                Name    = "RWSERVE",
                Url     = rwServlet is not null ? "(from rwservlet.properties)" : null,
                Version = "Discovered",
            });
        }

        var reportsConf = FindFile(reportsHome, "reports_server.conf");
        if (reportsConf is not null)
        {
            var text = SafeDiscoveryIO.ReadAllTextSafe(reportsConf);
            if (text?.Contains("ORACLE_HOME", StringComparison.OrdinalIgnoreCase) == true)
            {
                servers.Add(new ReportsServerDescriptor { Name = "REPORTS_SERVER", Version = "Configured" });
            }
        }

        return (existing, servers);
    }

    private static string? FindFile(string root, string fileName)
    {
        try { return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault(); }
        catch { return null; }
    }

    private static string? ResolveReportsHome(string? middlewareHome)
    {
        if (string.IsNullOrWhiteSpace(middlewareHome)) return null;
        var candidates = new[]
        {
            Path.Combine(middlewareHome, "reports"),
            Path.Combine(middlewareHome, "Oracle_BI", "reports"),
            Path.Combine(middlewareHome, "instances"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }
}
