namespace WEDM.Engine.Wlst;

/// <summary>Resolves middleware-home-relative paths across WebLogic 11g / 12c / 14c layouts.</summary>
public static class MiddlewareHomePathResolver
{
    public static IReadOnlyList<string> GetWlsTemplateJarCandidates(string middlewareHome)
        => WithLayouts(middlewareHome, "common", "templates", "wls", "wls.jar");

    public static IReadOnlyList<string> GetNodeManagerDomainsFileCandidates(string middlewareHome)
        => WithLayouts(middlewareHome, "common", "nodemanager", "nodemanager.domains");

    public static IReadOnlyList<string> GetInstallNodeMgrSvcCandidates(string middlewareHome)
        => WithLayouts(middlewareHome, "server", "bin", "installNodeMgrSvc.cmd");

    public static string ResolveExistingOrDefault(IReadOnlyList<string> candidates)
        => candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault() ?? string.Empty;

    private static IReadOnlyList<string> WithLayouts(string middlewareHome, params string[] tail)
    {
        if (string.IsNullOrWhiteSpace(middlewareHome))
            return [];

        var segments = new[] { "wlserver", "wlserver_10.3", "oracle_common" };
        return segments
            .Select(layout => Path.Combine(new[] { middlewareHome, layout }.Concat(tail).ToArray()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
