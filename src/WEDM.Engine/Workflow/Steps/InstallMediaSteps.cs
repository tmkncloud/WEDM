using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Payload;
using WEDM.Infrastructure.Registry;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>Silent JDK installation via msiexec or vendor silent .exe (elevated).</summary>
public sealed class InstallJdkStep : IStepExecutor
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService _log;
    private readonly IPayloadAcquisitionService _payloads;

    public InstallJdkStep(
        IPowerShellExecutor ps,
        ILoggingService log,
        IPayloadAcquisitionService payloads)
    {
        _ps       = ps;
        _log      = log;
        _payloads = payloads;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!config.Components.HasFlag(InstallationComponents.JDK))
            return StepExecutionResult.Ok("JDK installation not selected — skipped.", sw.Elapsed);

        if (config.PayloadAcquisition.SkipInstallWhenPresent && _payloads.TryDetectCompatibleJdk(config, out var existing))
        {
            config.Java.JavaHome = existing!;
            _log.Info($"JDK already installed at {existing} — skipped.", "Install.JDK");
            return StepExecutionResult.Ok($"Already Installed — JDK at {existing}", sw.Elapsed);
        }

        var resolved = await _payloads.EnsureJdkInstallerAsync(config, cancellationToken).ConfigureAwait(false);
        if (resolved.Status == PayloadResolutionStatus.AlreadyInstalled)
            return StepExecutionResult.Ok(resolved.Message, sw.Elapsed);
        if (!resolved.Success || string.IsNullOrWhiteSpace(resolved.InstallerPath))
            return StepExecutionResult.Fail(resolved.Message);

        var installer = resolved.InstallerPath;
        Directory.CreateDirectory(config.Java.InstallDirectory);
        var inst  = PsSingleQuote(Path.GetFullPath(config.Java.InstallDirectory));
        var media = PsSingleQuote(Path.GetFullPath(installer));

        string body;
        if (installer.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
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

        _log.Info($"Starting JDK silent installation ({resolved.Status}): {installer}", "Install.JDK");
        var result = await _ps.ExecuteCommandAsync(
            body.Trim(),
            workingDirectory: config.Paths.TempDirectory,
            runAsAdministrator: true,
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(45));

        sw.Stop();
        if (result.TimedOut)
            return StepExecutionResult.Fail("JDK installer timed out.", -2);
        return InstallerExitCodes.ToStepResult(result.ExitCode, "JDK", sw.Elapsed, config.Java.InstallDirectory);
    }

    private static string PsSingleQuote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>Visual C++ x64 redistributable silent install.</summary>
public sealed class InstallVcRedistStep : IStepExecutor
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService _log;
    private readonly IPayloadAcquisitionService _payloads;

    public InstallVcRedistStep(
        IPowerShellExecutor ps,
        ILoggingService log,
        IPayloadAcquisitionService payloads)
    {
        _ps       = ps;
        _log      = log;
        _payloads = payloads;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!config.Components.HasFlag(InstallationComponents.VCRedist))
            return StepExecutionResult.Ok("VC++ installation not selected — skipped.", sw.Elapsed);

        if (config.PayloadAcquisition.SkipInstallWhenPresent && _payloads.IsVcRedistInstalled())
        {
            _log.Info("VC++ Redistributable already installed — skipped.", "Install.VC");
            return StepExecutionResult.Ok("Already Installed — Visual C++ Redistributable", sw.Elapsed);
        }

        var resolved = await _payloads.EnsureVcRedistInstallerAsync(config, cancellationToken).ConfigureAwait(false);
        if (resolved.Status == PayloadResolutionStatus.AlreadyInstalled)
            return StepExecutionResult.Ok(resolved.Message, sw.Elapsed);
        if (!resolved.Success || string.IsNullOrWhiteSpace(resolved.InstallerPath))
            return StepExecutionResult.Fail(resolved.Message);

        var exe = PsSingleQuote(Path.GetFullPath(resolved.InstallerPath));
        var body = $@"
$p = Start-Process -FilePath {exe} -ArgumentList @('/install', '/quiet', '/norestart') -Wait -PassThru -NoNewWindow
exit $(if ($null -eq $p) {{ 1 }} else {{ $p.ExitCode }})
";

        _log.Info($"Installing VC++ Redistributable ({resolved.Status}).", "Install.VC");
        var result = await _ps.ExecuteCommandAsync(
            body.Trim(),
            workingDirectory: config.Paths.TempDirectory,
            runAsAdministrator: true,
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(20));

        sw.Stop();
        if (result.TimedOut)
            return StepExecutionResult.Fail("VC++ installer timed out.", -2);
        return InstallerExitCodes.ToStepResult(result.ExitCode, "VC++", sw.Elapsed);
    }

    private static string PsSingleQuote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>Sets JAVA_HOME and PATH after JDK payload exists (requires admin / WEDM elevated).</summary>
public sealed class ConfigureJavaHomeStep : IStepExecutor
{
    private readonly WindowsRegistryService _registry;
    private readonly ILoggingService _log;
    private readonly IPayloadAcquisitionService _payloads;

    public ConfigureJavaHomeStep(
        WindowsRegistryService registry,
        ILoggingService log,
        IPayloadAcquisitionService payloads)
    {
        _registry = registry;
        _log      = log;
        _payloads = payloads;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!config.Components.HasFlag(InstallationComponents.JDK))
            return Task.FromResult(StepExecutionResult.Ok("JAVA_HOME configuration skipped (JDK not selected).", sw.Elapsed));

        var javaHome = ResolveJavaHome(config);
        if (javaHome is null || !Directory.Exists(javaHome))
        {
            if (_payloads.TryDetectCompatibleJdk(config, out var detected))
                javaHome = detected;
        }

        if (javaHome is null || !Directory.Exists(javaHome))
            return Task.FromResult(StepExecutionResult.Fail("Could not resolve JAVA_HOME after JDK install."));

        config.Java.JavaHome = javaHome;
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
