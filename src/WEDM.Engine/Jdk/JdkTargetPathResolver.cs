using WEDM.Domain.Models;

namespace WEDM.Engine.Jdk;

/// <summary>Resolves the target JAVA_HOME directory for silent JDK installation.</summary>
public static class JdkTargetPathResolver
{
    public static string ResolveTargetJavaHome(DeploymentConfiguration config)
    {
        var baseDir = string.IsNullOrWhiteSpace(config.Java.InstallDirectory)
            ? @"C:\Program Files\Java"
            : config.Java.InstallDirectory.TrimEnd('\\', '/');

        var ver = config.Java.JdkVersion?.Trim();
        if (string.IsNullOrWhiteSpace(ver))
            return Path.Combine(baseDir, "jdk");

        // Oracle 8u202 typical folder: jdk1.8.0_202
        if (ver.StartsWith("1.", StringComparison.Ordinal))
            return Path.Combine(baseDir, $"jdk{ver}");

        // Temurin-style: jdk-21.0.4+7 or 21.0.4 -> jdk-21.0.4
        if (char.IsDigit(ver[0]))
            return Path.Combine(baseDir, $"jdk-{ver.TrimStart('j', 'd', 'k', '-')}");

        return Path.Combine(baseDir, ver);
    }
}
