using System.Text;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Registry;

namespace WEDM.Engine.Jdk;

/// <summary>Production JDK installation orchestration — strategy selection, execution, validation, diagnostics.</summary>
public sealed class JdkInstallationService
{
    private readonly IPowerShellExecutor          _ps;
    private readonly ILoggingService              _log;
    private readonly IPayloadAcquisitionService _payloads;
    private readonly JdkInstallerStrategyFactory  _strategies;
    private readonly JdkInstallValidator          _validator;

    public JdkInstallationService(
        IPowerShellExecutor ps,
        ILoggingService log,
        IPayloadAcquisitionService payloads,
        JdkInstallerStrategyFactory strategies,
        WindowsRegistryService registry)
    {
        _ps          = ps;
        _log         = log;
        _payloads    = payloads;
        _strategies  = strategies;
        _validator   = new JdkInstallValidator(registry);
    }

    public async Task<StepExecutionResult> InstallAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw         = System.Diagnostics.Stopwatch.StartNew();
        var diagnostics = new JdkInstallationDiagnostics();

        if (!config.Components.HasFlag(InstallationComponents.JDK))
            return StepExecutionResult.Ok("JDK installation not selected — skipped.", sw.Elapsed);

        if (config.PayloadAcquisition.SkipInstallWhenPresent && _payloads.TryDetectCompatibleJdk(config, out var existing))
        {
            config.Java.JavaHome = existing!;
            diagnostics.SkippedAlreadyInstalled = true;
            diagnostics.PreExistingJavaHome     = existing;
            diagnostics.ResolvedJavaHome        = existing;
            diagnostics.Success                 = true;
            diagnostics.NormalizedStatus        = nameof(JdkInstallNormalizedStatus.AlreadyInstalled);
            diagnostics.NormalizedMessage       = $"Compatible JDK already installed at {existing}";
            config.Java.LastInstallationDiagnostics = diagnostics;
            _log.Info(diagnostics.NormalizedMessage, "Install.JDK");
            return StepExecutionResult.Ok($"Already Installed — JDK at {existing}", sw.Elapsed);
        }

        var resolved = await _payloads.EnsureJdkInstallerAsync(config, cancellationToken).ConfigureAwait(false);
        if (resolved.Status == PayloadResolutionStatus.AlreadyInstalled)
        {
            diagnostics.SkippedAlreadyInstalled = true;
            diagnostics.NormalizedMessage         = resolved.Message;
            config.Java.LastInstallationDiagnostics = diagnostics;
            return StepExecutionResult.Ok(resolved.Message, sw.Elapsed);
        }

        if (!resolved.Success || string.IsNullOrWhiteSpace(resolved.InstallerPath))
            return StepExecutionResult.Fail(resolved.Message);

        var installerPath = resolved.InstallerPath!;
        IJdkInstallerStrategy strategy;
        try
        {
            strategy = _strategies.Resolve(installerPath);
        }
        catch (NotSupportedException ex)
        {
            diagnostics.NormalizedMessage = ex.Message;
            config.Java.LastInstallationDiagnostics = diagnostics;
            return StepExecutionResult.Fail(ex.Message);
        }

        var invocation = strategy.BuildInvocation(config, installerPath);

        diagnostics.InstallerType    = strategy.StrategyName;
        diagnostics.InstallerPath    = installerPath;
        diagnostics.TargetJavaHome   = invocation.TargetJavaHome;
        diagnostics.ArgumentsDisplay = invocation.DisplayCommandLine;
        diagnostics.WorkingDirectory = invocation.WorkingDirectory;

        _log.Info($"JDK install strategy: {strategy.StrategyName}", "Install.JDK");
        _log.Info($"Installer executable: {invocation.ProcessPath}", "Install.JDK");
        _log.Info($"Target JAVA_HOME: {invocation.TargetJavaHome}", "Install.JDK");
        _log.Info($"Argument list: {string.Join(" | ", invocation.Arguments)}", "Install.JDK");
        _log.Info($"Working directory: {invocation.WorkingDirectory}", "Install.JDK");

        Directory.CreateDirectory(invocation.WorkingDirectory);
        var targetParent = Path.GetDirectoryName(invocation.TargetJavaHome);
        if (!string.IsNullOrWhiteSpace(targetParent))
            Directory.CreateDirectory(targetParent);
        var script = BuildElevatedInstallScript(invocation);
        _log.Verbose($"Elevated install script:\n{script}", "Install.JDK");

        var result = await _ps.ExecuteCommandAsync(
            script,
            workingDirectory: invocation.WorkingDirectory,
            runAsAdministrator: true,
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(45)).ConfigureAwait(false);

        sw.Stop();

        diagnostics.RawExitCode      = result.ExitCode;
        diagnostics.InstallerStdout  = Truncate(result.Output, 4000);
        diagnostics.InstallerStderr  = Truncate(result.Errors, 4000);

        if (result.TimedOut)
        {
            diagnostics.NormalizedStatus  = nameof(JdkInstallNormalizedStatus.TimedOut);
            diagnostics.NormalizedMessage = "JDK installer timed out.";
            config.Java.LastInstallationDiagnostics = diagnostics;
            return StepExecutionResult.Fail(diagnostics.NormalizedMessage, -2);
        }

        var normalized = JdkExitCodeNormalizer.Normalize(result.ExitCode);
        diagnostics.NormalizedStatus  = normalized.Status.ToString();
        diagnostics.NormalizedMessage = normalized.Message;
        diagnostics.RebootRequired    = normalized.RebootRequired;

        _log.Info(
            $"Installer exit code: {result.ExitCode} → {normalized.Status}: {normalized.Message}",
            "Install.JDK");

        if (!string.IsNullOrWhiteSpace(result.Output))
            _log.Info($"Installer stdout: {Truncate(result.Output, 2000)}", "Install.JDK");
        if (!string.IsNullOrWhiteSpace(result.Errors))
            _log.Warning($"Installer stderr: {Truncate(result.Errors, 2000)}", "Install.JDK");

        var validation = _validator.Validate(invocation.TargetJavaHome, config.Java.InstallDirectory);
        diagnostics.ValidationChecks = validation.Checks.ToList();
        diagnostics.JavaVersionOutput = validation.JavaVersionOutput;

        if (validation.Passed && validation.JavaHome is not null)
        {
            config.Java.JavaHome         = validation.JavaHome;
            diagnostics.ResolvedJavaHome = validation.JavaHome;
            diagnostics.Success          = true;
            config.Java.LastInstallationDiagnostics = diagnostics;

            foreach (var check in validation.Checks)
                _log.Info(check, "Install.JDK.Validate");

            var msg = normalized.Success
                ? $"JDK installed at {validation.JavaHome}"
                : $"JDK present at {validation.JavaHome} (installer exit {result.ExitCode} ignored after file validation).";

            _log.Info(msg, "Install.JDK");
            return StepExecutionResult.Ok(msg, sw.Elapsed);
        }

        diagnostics.Success = false;
        config.Java.LastInstallationDiagnostics = diagnostics;

        foreach (var check in validation.Checks)
            _log.Warning(check, "Install.JDK.Validate");

        if (normalized.Status == JdkInstallNormalizedStatus.InvalidArguments)
        {
            return StepExecutionResult.Fail(
                $"JDK install failed — {normalized.Message} Validation: {SummarizeValidation(validation)}",
                result.ExitCode);
        }

        return StepExecutionResult.Fail(
            $"JDK install failed (exit {result.ExitCode}): {normalized.Message}. {SummarizeValidation(validation)}",
            result.ExitCode);
    }

    internal static string BuildElevatedInstallScript(JdkInstallInvocation invocation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$exe = '{EscapePs(invocation.ProcessPath)}'");
        sb.AppendLine($"$argList = @({string.Join(", ", invocation.Arguments.Select(a => $"'{EscapePs(a)}'"))})");
        sb.AppendLine("$outLog = Join-Path $env:TEMP 'wedm_jdk_out.txt'");
        sb.AppendLine("$errLog = Join-Path $env:TEMP 'wedm_jdk_err.txt'");
        sb.AppendLine("$p = Start-Process -FilePath $exe -ArgumentList $argList -Wait -PassThru -NoNewWindow `");
        sb.AppendLine("    -RedirectStandardOutput $outLog -RedirectStandardError $errLog");
        sb.AppendLine("Get-Content $outLog -ErrorAction SilentlyContinue | Write-Output");
        sb.AppendLine("Get-Content $errLog -ErrorAction SilentlyContinue | Write-Warning");
        sb.AppendLine("exit $(if ($null -eq $p) { 1 } else { $p.ExitCode })");
        return sb.ToString();
    }

    private static string EscapePs(string s)
        => s.Replace("'", "''", StringComparison.Ordinal);

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string SummarizeValidation(JdkInstallValidationResult validation)
        => string.Join("; ", validation.Checks.Where(c => c.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase)));
}
