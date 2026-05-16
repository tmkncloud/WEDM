using WEDM.Domain.Models;

namespace WEDM.Engine.Jdk.Strategies;

/// <summary>Fallback for unknown EXE installers — minimal /s INSTALLDIR (last resort).</summary>
public sealed class GenericExeJdkInstallerStrategy : IJdkInstallerStrategy
{
    public string StrategyName => "GenericExe";

    public bool CanHandle(string installerPath)
        => JdkInstallerPathNormalizer.IsExe(installerPath);

    public string ResolveTargetJavaHome(DeploymentConfiguration config)
        => JdkTargetPathResolver.ResolveTargetJavaHome(config);

    public JdkInstallInvocation BuildInvocation(DeploymentConfiguration config, string installerPath)
    {
        var target = ResolveTargetJavaHome(config);
        var quoted = target.Contains(' ', StringComparison.Ordinal) ? $"\"{target}\"" : target;

        return new JdkInstallInvocation
        {
            StrategyName     = StrategyName,
            ProcessPath      = Path.GetFullPath(installerPath),
            WorkingDirectory = config.Paths.TempDirectory,
            TargetJavaHome   = target,
            Arguments = ["/s", $"INSTALLDIR={quoted}"]
        };
    }
}
