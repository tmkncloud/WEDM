using WEDM.Domain.Enums;
using WEDM.Engine.Versioning;

namespace WEDM.Engine.Wlst;

/// <summary>Central WLST launcher resolution for 11g / 12c / 14c middleware homes.</summary>
public static class WlstPathResolver
{
    public static IReadOnlyList<string> GetCandidates(string middlewareHome, WebLogicVersion? version = null)
    {
        if (string.IsNullOrWhiteSpace(middlewareHome))
            return [];

        if (version.HasValue)
            return WebLogicVersionAdapterFactory.For(version.Value).WlstCmdCandidates(middlewareHome);

        return
        [
            Path.Combine(middlewareHome, "oracle_common", "common", "bin", "wlst.cmd"),
            Path.Combine(middlewareHome, "wlserver", "common", "bin", "wlst.cmd"),
            Path.Combine(middlewareHome, "wlserver_10.3", "common", "bin", "wlst.cmd"),
        ];
    }

    public static string Resolve(string middlewareHome, WebLogicVersion? version = null)
    {
        var candidates = GetCandidates(middlewareHome, version);
        return candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault() ?? string.Empty;
    }

    public static bool TryResolve(string middlewareHome, out string wlstPath, WebLogicVersion? version = null)
    {
        wlstPath = Resolve(middlewareHome, version);
        return !string.IsNullOrWhiteSpace(wlstPath) && File.Exists(wlstPath);
    }
}
