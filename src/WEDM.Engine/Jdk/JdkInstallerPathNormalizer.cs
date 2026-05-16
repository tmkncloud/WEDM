namespace WEDM.Engine.Jdk;

/// <summary>Normalizes installer paths and shared filename patterns (e.g. jdk-*.exe).</summary>
internal static class JdkInstallerPathNormalizer
{
    public static string Normalize(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
            return string.Empty;

        var trimmed = installerPath.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    public static string GetFileName(string installerPath)
        => Path.GetFileName(Normalize(installerPath));

    public static bool IsExe(string installerPath)
    {
        var name = GetFileName(installerPath);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMsi(string installerPath)
    {
        var name = GetFileName(installerPath);
        return name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Matches Oracle-style payloads: jdk-8u202-windows-x64.exe, jdk-11.0.2-x64.exe, etc.</summary>
    public static bool MatchesJdkExePattern(string installerPath)
    {
        if (!IsExe(installerPath))
            return false;

        var name = GetFileName(installerPath).ToLowerInvariant();
        return name.StartsWith("jdk-", StringComparison.Ordinal)
            || (name.Contains("jdk") && name.Contains("windows"));
    }
}
