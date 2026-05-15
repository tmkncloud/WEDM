using WEDM.Domain.Models;

namespace WEDM.Engine.Discovery.Parsers;

public static class NodeManagerConfigParser
{
    public static void ApplyNodeManagerSettings(DomainAnalysisSnapshot analysis, string domainHome)
    {
        var candidates = new[]
        {
            Path.Combine(domainHome, "nodemanager", "nodemanager.properties"),
            Path.Combine(domainHome, "common", "nodemanager", "nodemanager.properties"),
            Path.Combine(domainHome, "config", "nodemanager", "nodemanager.properties"),
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return;

        analysis.NodeManagerPropertiesPath = path;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
                var parts = trimmed.Split('=', 2);
                if (parts.Length != 2) continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim();

                if (key.Equals("SecureListener", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("ListenSecure", StringComparison.OrdinalIgnoreCase))
                {
                    analysis.NodeManagerSecure = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            /* read-only best effort */
        }
    }
}
