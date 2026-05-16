using WEDM.Domain.Models;

namespace WEDM.Engine.Jdk.Strategies;

/// <summary>Eclipse Temurin / Adoptium MSI with optional feature properties.</summary>
public sealed class TemurinMsiInstallerStrategy : IJdkInstallerStrategy
{
    public string StrategyName => "TemurinMsi";

    public bool CanHandle(string installerPath)
    {
        if (!installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            return false;
        var name = Path.GetFileName(installerPath).ToLowerInvariant();
        return name.Contains("temurin") || name.Contains("adoptium") || name.Contains("openjdk");
    }

    public string ResolveTargetJavaHome(DeploymentConfiguration config)
        => JdkTargetPathResolver.ResolveTargetJavaHome(config);

    public JdkInstallInvocation BuildInvocation(DeploymentConfiguration config, string installerPath)
    {
        var target = ResolveTargetJavaHome(config);
        var msi    = Path.GetFullPath(installerPath);

        return new JdkInstallInvocation
        {
            StrategyName     = StrategyName,
            ProcessPath      = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
            WorkingDirectory = config.Paths.TempDirectory,
            TargetJavaHome   = target,
            Arguments =
            [
                "/i", msi,
                "/qn",
                "/norestart",
                "REBOOT=ReallySuppress",
                "ADDLOCAL=FeatureMain,FeatureEnvironment,FeatureJarFileRunWith,FeatureJavaHome",
                $"INSTALLDIR={target}"
            ]
        };
    }
}
