namespace WEDM.Engine.Wlst;

/// <summary>Central WLST launcher resolution for 11g / 12c / 14c middleware homes.</summary>
public static class WlstPathResolver
{
    public static IReadOnlyList<string> GetCandidates(string middlewareHome)
    {
        if (string.IsNullOrWhiteSpace(middlewareHome))
            return [];

        return
        [
            Path.Combine(middlewareHome, "oracle_common", "common", "bin", "wlst.cmd"),
            Path.Combine(middlewareHome, "wlserver", "common", "bin", "wlst.cmd"),
            Path.Combine(middlewareHome, "wlserver_10.3", "common", "bin", "wlst.cmd"),
        ];
    }

    public static string Resolve(string middlewareHome)
    {
        var candidates = GetCandidates(middlewareHome);
        return candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault() ?? string.Empty;
    }

    public static bool TryResolve(string middlewareHome, out string wlstPath)
    {
        wlstPath = Resolve(middlewareHome);
        return !string.IsNullOrWhiteSpace(wlstPath) && File.Exists(wlstPath);
    }
}
