using WEDM.Domain.Models;

namespace WEDM.Engine.Jdk.Strategies;

/// <summary>Generic MSI-based JDK installers (Temurin, OpenJDK, vendor MSI).</summary>
public sealed class MsiJdkInstallerStrategy : IJdkInstallerStrategy
{
    public string StrategyName => "MsiJdk";

    public bool CanHandle(string installerPath)
        => installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

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
                $"INSTALLDIR={target}",
                $"TARGETDIR={target}"
            ]
        };
    }
}
