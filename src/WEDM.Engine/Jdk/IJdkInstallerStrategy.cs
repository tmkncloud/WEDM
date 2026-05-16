using WEDM.Domain.Models;

namespace WEDM.Engine.Jdk;

/// <summary>Installer-specific silent installation logic for JDK payloads.</summary>
public interface IJdkInstallerStrategy
{
    string StrategyName { get; }

    bool CanHandle(string installerPath);

    JdkInstallInvocation BuildInvocation(DeploymentConfiguration config, string installerPath);

    string ResolveTargetJavaHome(DeploymentConfiguration config);
}

/// <summary>Executable, arguments, and metadata for a single elevated install invocation.</summary>
public sealed class JdkInstallInvocation
{
    public string StrategyName { get; init; } = string.Empty;
    public string ProcessPath { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = string.Empty;
    public string TargetJavaHome { get; init; } = string.Empty;

    public string DisplayCommandLine =>
        $"\"{ProcessPath}\" {string.Join(" ", Arguments.Select(QuoteForDisplay))}";

    private static string QuoteForDisplay(string arg)
        => arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg;
}
