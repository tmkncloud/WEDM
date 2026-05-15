using WEDM.Domain.Models;

namespace WEDM.Engine.Discovery.Scanners;

/// <summary>Metadata-level Forms environment scanner (read-only, binary-safe .fmb analysis).</summary>
public static class FormsMetadataScanner
{
    public static FormsReportsMetadataSnapshot Scan(string? formsHome, string? middlewareHome)
    {
        var snapshot = new FormsReportsMetadataSnapshot();
        formsHome ??= ResolveFormsHome(middlewareHome);
        if (string.IsNullOrWhiteSpace(formsHome) || !Directory.Exists(formsHome))
            return snapshot;

        snapshot.ConfigurationPath = formsHome;

        var moduleDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int fmb = 0, mmb = 0, pll = 0, olb = 0, menus = 0;
        var webUtilModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in SafeDiscoveryIO.EnumerateFilesSafe(formsHome, "*.*", 8000))
        {
            var ext = Path.GetExtension(file);
            switch (ext.ToLowerInvariant())
            {
                case ".fmb": fmb++; moduleDirs.Add(Path.GetDirectoryName(file) ?? ""); break;
                case ".mmb": mmb++; break;
                case ".pll": pll++; break;
                case ".olb": olb++; break;
                case ".mmn":
                case ".mnl": menus++; break;
            }

            if (ext.Equals(".fmb", StringComparison.OrdinalIgnoreCase) && File.Exists(file))
            {
                var deps = FormsBinaryScanner.DetectDependencies(file);
                if (deps.Any(d => d.Contains("WEBUTIL", StringComparison.OrdinalIgnoreCase)))
                    webUtilModules.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        ScanConfigFiles(formsHome, snapshot, webUtilModules);

        snapshot.FormCount              = fmb;
        snapshot.MenuCount              = Math.Max(menus, mmb);
        snapshot.ModuleCount            = moduleDirs.Count(m => !string.IsNullOrWhiteSpace(m));
        snapshot.CustomPlsqlLibraries   = pll;
        snapshot.UsesWebUtil            = webUtilModules.Count > 0;
        snapshot.WebUtilModuleCount     = webUtilModules.Count;
        snapshot.UsesOracleGraphics     = olb > 0;
        snapshot.TopModules             = moduleDirs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar)) ?? d)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(8)
            .ToList();

        return snapshot;
    }

    private static void ScanConfigFiles(string formsHome, FormsReportsMetadataSnapshot snapshot, HashSet<string> webUtilModules)
    {
        var formsweb = FindFile(formsHome, "formsweb.cfg");
        if (formsweb is not null)
        {
            var text = SafeDiscoveryIO.ReadAllTextSafe(formsweb, 512_000);
            if (text is not null && text.Contains("webutil", StringComparison.OrdinalIgnoreCase))
                snapshot.UsesWebUtil = true;
        }

        var defaultEnv = FindFile(formsHome, "default.env");
        if (defaultEnv is not null)
            snapshot.ConfigurationPath = Path.GetDirectoryName(defaultEnv) ?? snapshot.ConfigurationPath;
    }

    private static string? FindFile(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResolveFormsHome(string? middlewareHome)
    {
        if (string.IsNullOrWhiteSpace(middlewareHome)) return null;
        var candidates = new[]
        {
            Path.Combine(middlewareHome, "forms"),
            Path.Combine(middlewareHome, "Oracle_Home", "forms"),
            Path.Combine(middlewareHome, "Oracle_FRHome1", "forms"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }
}
