using System.Xml.Linq;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Automation;
using WEDM.Infrastructure.Registry;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>Silent JDK installation via msiexec or vendor silent .exe (elevated).</summary>
public sealed class InstallJdkStep : IStepExecutor
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService     _log;

    public InstallJdkStep(IPowerShellExecutor ps, ILoggingService log)
    {
        _ps  = ps;
        _log = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(config.JdkInstallerPath) || !File.Exists(config.JdkInstallerPath))
        {
            return StepExecutionResult.Fail(
                "jdkInstallerPath must be set to an existing JDK .msi or silent-setup .exe (see deployment JSON / wizard).");
        }

        Directory.CreateDirectory(config.Java.InstallDirectory);
        var inst = PsSingleQuote(Path.GetFullPath(config.Java.InstallDirectory));
        var media = PsSingleQuote(Path.GetFullPath(config.JdkInstallerPath));

        string body;
        if (config.JdkInstallerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            body = $@"
$msi = {media}
$dir = {inst}
$args = @('/i', $msi, '/qn', '/norestart', 'REBOOT=ReallySuppress', ""INSTALLDIR=$dir"")
$p = Start-Process -FilePath msiexec.exe -ArgumentList $args -Wait -PassThru -NoNewWindow
exit $(if ($null -eq $p) {{ 1 }} else {{ $p.ExitCode }})
";
        }
        else
        {
            body = $@"
$exe = {media}
$dir = {inst}
$p = Start-Process -FilePath $exe -ArgumentList @('/s', ""INSTALLDIR=$dir"") -Wait -PassThru -NoNewWindow
exit $(if ($null -eq $p) {{ 1 }} else {{ $p.ExitCode }})
";
        }

        _log.Info("Starting JDK silent installation (elevated).", "Install.JDK");
        var result = await _ps.ExecuteCommandAsync(
            body.Trim(),
            workingDirectory: config.Paths.TempDirectory,
            runAsAdministrator: true,
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(45));

        sw.Stop();
        if (result.TimedOut)
            return StepExecutionResult.Fail("JDK installer timed out.", -2);
        if (result.ExitCode != 0)
            return StepExecutionResult.Fail($"JDK install failed (exit {result.ExitCode}): {result.Errors}", result.ExitCode);

        return StepExecutionResult.Ok($"JDK installed under {config.Java.InstallDirectory}", sw.Elapsed);
    }

    private static string PsSingleQuote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>Visual C++ x64 redistributable silent install.</summary>
public sealed class InstallVcRedistStep : IStepExecutor
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService     _log;

    public InstallVcRedistStep(IPowerShellExecutor ps, ILoggingService log)
    {
        _ps  = ps;
        _log = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(config.VcRedistX64InstallerPath) ||
            !File.Exists(config.VcRedistX64InstallerPath))
        {
            return StepExecutionResult.Fail(
                "vcRedistX64InstallerPath must point to Microsoft vc_redist.x64.exe (offline installer).");
        }

        var exe = PsSingleQuote(Path.GetFullPath(config.VcRedistX64InstallerPath));
        var body = $@"
$p = Start-Process -FilePath {exe} -ArgumentList @('/install', '/quiet', '/norestart') -Wait -PassThru -NoNewWindow
exit $(if ($null -eq $p) {{ 1 }} else {{ $p.ExitCode }})
";

        _log.Info("Installing VC++ Redistributable x64 (elevated).", "Install.VC");
        var result = await _ps.ExecuteCommandAsync(
            body.Trim(),
            workingDirectory: config.Paths.TempDirectory,
            runAsAdministrator: true,
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(20));

        sw.Stop();
        if (result.TimedOut)
            return StepExecutionResult.Fail("VC++ installer timed out.", -2);
        if (result.ExitCode != 0)
            return StepExecutionResult.Fail($"VC++ install failed (exit {result.ExitCode}): {result.Errors}", result.ExitCode);

        return StepExecutionResult.Ok("VC++ x64 redistributable installed.", sw.Elapsed);
    }

    private static string PsSingleQuote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>Sets JAVA_HOME and PATH after JDK payload exists (requires admin / WEDM elevated).</summary>
public sealed class ConfigureJavaHomeStep : IStepExecutor
{
    private readonly WindowsRegistryService _registry;
    private readonly ILoggingService        _log;

    public ConfigureJavaHomeStep(WindowsRegistryService registry, ILoggingService log)
    {
        _registry = registry;
        _log      = log;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var javaHome = ResolveJavaHome(config);
        if (javaHome is null || !Directory.Exists(javaHome))
            return Task.FromResult(StepExecutionResult.Fail("Could not resolve JAVA_HOME after JDK install."));

        _registry.SetSystemEnvironmentVariable("JAVA_HOME", javaHome);
        _registry.AppendToSystemPath(Path.Combine(javaHome, "bin"));
        sw.Stop();
        _log.Info($"JAVA_HOME set to {javaHome}", "Install.JDK");
        return Task.FromResult(StepExecutionResult.Ok(javaHome, sw.Elapsed));
    }

    private static string? ResolveJavaHome(DeploymentConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.Java.JavaHome) && Directory.Exists(config.Java.JavaHome))
            return config.Java.JavaHome;

        if (!Directory.Exists(config.Java.InstallDirectory)) return null;

        var nested = Directory.GetDirectories(config.Java.InstallDirectory, "jdk*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetDirectories(config.Java.InstallDirectory, "jdk-*", SearchOption.TopDirectoryOnly))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "bin", "java.exe")));

        if (nested is not null) return nested;

        return File.Exists(Path.Combine(config.Java.InstallDirectory, "bin", "java.exe"))
            ? config.Java.InstallDirectory
            : null;
    }
}
