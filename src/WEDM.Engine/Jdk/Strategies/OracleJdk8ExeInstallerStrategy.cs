using WEDM.Domain.Models;

namespace WEDM.Engine.Jdk.Strategies;

/// <summary>Oracle JDK 8 Windows EXE (e.g. jdk-8u202-windows-x64.exe) silent install.</summary>
public sealed class OracleJdk8ExeInstallerStrategy : IJdkInstallerStrategy
{
    public string StrategyName => "OracleJdk8Exe";

    public bool CanHandle(string installerPath)
    {
        if (!JdkInstallerPathNormalizer.IsExe(installerPath))
            return false;

        if (JdkInstallerPathNormalizer.MatchesJdkExePattern(installerPath))
            return true;

        var name = JdkInstallerPathNormalizer.GetFileName(installerPath).ToLowerInvariant();
        return name.Contains("jdk")
            && (name.Contains("8u") || name.Contains("1.8") || name.Contains("windows"));
    }

    public string ResolveTargetJavaHome(DeploymentConfiguration config)
        => JdkTargetPathResolver.ResolveTargetJavaHome(config);

    public JdkInstallInvocation BuildInvocation(DeploymentConfiguration config, string installerPath)
    {
        var target = ResolveTargetJavaHome(config);

        return new JdkInstallInvocation
        {
            StrategyName     = StrategyName,
            ProcessPath      = Path.GetFullPath(installerPath),
            WorkingDirectory = config.Paths.TempDirectory,
            TargetJavaHome   = target,
            Arguments =
            [
                "/s",
                "INSTALL_SILENT=Enable",
                "AUTO_UPDATE=Disable",
                "WEB_JAVA=Disable",
                "WEB_JAVA_SECURITY_LEVEL=VH",
                "SPONSORS=Disable",
                "REMOVEOUTOFDATEJRES=1",
                $"INSTALLDIR={QuoteInstallDir(target)}"
            ]
        };
    }

    private static string QuoteInstallDir(string path)
    {
        // Oracle installer accepts quoted INSTALLDIR for paths with spaces.
        return path.Contains(' ', StringComparison.Ordinal)
            ? $"\"{path}\""
            : path;
    }
}
