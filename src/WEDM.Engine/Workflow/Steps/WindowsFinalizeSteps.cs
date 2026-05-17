using System.Text;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Wlst;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>
/// Registers Oracle WebLogic Windows services (Node Manager, optional domain service hooks).
/// </summary>
public sealed class RegisterWindowsServicesStep : IStepExecutor
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService     _log;

    public RegisterWindowsServicesStep(IPowerShellExecutor ps, ILoggingService log)
    {
        _ps  = ps;
        _log = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (step.Name.Equals("RegisterNodeMgrService", StringComparison.OrdinalIgnoreCase))
            return await RegisterNodeManagerAsync(config, cancellationToken).ConfigureAwait(false);

        if (step.Name.Equals("RegisterAdminService", StringComparison.OrdinalIgnoreCase))
            return await RunDomainBinServiceInstallerAsync(config, cancellationToken, "AdminServer").ConfigureAwait(false);

        if (step.Name.StartsWith("Register", StringComparison.OrdinalIgnoreCase) &&
            step.Name.EndsWith("Service", StringComparison.OrdinalIgnoreCase))
            return await RunDomainBinServiceInstallerAsync(config, cancellationToken, "ManagedServer").ConfigureAwait(false);

        return StepExecutionResult.Fail($"Unhandled Windows service step: {step.Name}");
    }

    private async Task<StepExecutionResult> RegisterNodeManagerAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cmd = MiddlewareHomePathResolver.ResolveExistingOrDefault(
            MiddlewareHomePathResolver.GetInstallNodeMgrSvcCandidates(config.Paths.MiddlewareHome));
        if (string.IsNullOrEmpty(cmd) || !File.Exists(cmd))
        {
            return StepExecutionResult.Fail(
                $"installNodeMgrSvc.cmd not found at '{cmd}'. Middleware install may be incomplete.");
        }

        var cmdQ = "'" + cmd.Replace("'", "''", StringComparison.Ordinal) + "'";
        var body = $@"
Set-Location -LiteralPath (Split-Path -Parent {cmdQ})
$p = Start-Process -FilePath {cmdQ} -Wait -PassThru -NoNewWindow
exit $(if ($null -eq $p) {{ 1 }} else {{ $p.ExitCode }})
";

        _log.Info("Registering Node Manager as a Windows service (elevated).", "Services");
        var result = await _ps.ExecuteCommandAsync(
            body.Trim(),
            workingDirectory: Path.GetDirectoryName(cmd),
            runAsAdministrator: true,
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(15));

        sw.Stop();
        if (result.ExitCode != 0 && result.ExitCode != 3010) // 3010 = success reboot pending
            return StepExecutionResult.Fail($"Node Manager service registration failed: {result.Errors}", result.ExitCode);

        return StepExecutionResult.Ok("Node Manager service registration completed.", sw.Elapsed);
    }

    private async Task<StepExecutionResult> RunDomainBinServiceInstallerAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken,
        string context)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bin = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName, "bin");
        if (!Directory.Exists(bin))
            return StepExecutionResult.Fail($"Domain bin directory not found: {bin}");

        var binQ = "'" + bin.Replace("'", "''", StringComparison.Ordinal) + "'";
        var body = $@"
Set-Location -LiteralPath {binQ}
$svc = Get-ChildItem -LiteralPath {binQ} -Filter '*Svc*.cmd' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $svc) {{
  Write-Warning 'No *Svc*.cmd found in domain bin — use Oracle installSvc template for Windows services.'
  exit 0
}}
$p = Start-Process -FilePath cmd.exe -ArgumentList @('/c', '""' + $svc.FullName + '""') -Wait -PassThru -NoNewWindow
exit $(if ($null -eq $p) {{ 1 }} else {{ $p.ExitCode }})
";

        _log.Info($"Attempting Windows service install scripts in domain bin ({context}).", "Services");
        var result = await _ps.ExecuteCommandAsync(
            body.Trim(),
            workingDirectory: bin,
            runAsAdministrator: true,
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(15));

        sw.Stop();
        if (result.ExitCode != 0 && result.ExitCode != 3010)
            return StepExecutionResult.Fail($"Service install script failed: {result.Errors}", result.ExitCode);

        return StepExecutionResult.Ok(result.Output.Trim(), sw.Elapsed);
    }
}

public sealed class CreateDesktopShortcutsStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public CreateDesktopShortcutsStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.CreateDesktopShortcuts)
            return Task.FromResult(StepExecutionResult.Ok("Desktop shortcuts disabled — skipped."));

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            Directory.CreateDirectory(desktop);
            var adminUrl = $"http://{config.Network.Hostname}:{config.Domain.AdminPort}/console";
            var path = Path.Combine(desktop, "Oracle WebLogic Console.url");
            var ini = new StringBuilder();
            ini.AppendLine("[InternetShortcut]");
            ini.AppendLine($"URL={adminUrl}");
            File.WriteAllText(path, ini.ToString());
            _log.Info($"Shortcut written: {path}", "Finalization");
            return Task.FromResult(StepExecutionResult.Ok(path));
        }
        catch (Exception ex)
        {
            return Task.FromResult(StepExecutionResult.Fail($"Shortcut creation failed: {ex.Message}", 1, ex));
        }
    }
}

public sealed class GenerateDeploymentReportStep : IStepExecutor
{
    private readonly IDeploymentPlanAccessor         _plan;
    private readonly ILoggingService                 _log;
    private readonly IOracleProcessLifecycleService? _lifecycle;

    public GenerateDeploymentReportStep(
        IDeploymentPlanAccessor         plan,
        ILoggingService                 log,
        IOracleProcessLifecycleService? lifecycle = null)
    {
        _plan      = plan;
        _log       = log;
        _lifecycle = lifecycle;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(config.Paths.ReportsDirectory);

        var steps = _plan.CurrentSteps?.ToList() ?? new List<DeploymentStep>();
        var anyFailed = steps.Any(s => s.Status == StepStatus.Failed);
        var started = steps.Where(s => s.StartedAt.HasValue).Select(s => s.StartedAt!.Value).DefaultIfEmpty(DateTimeOffset.UtcNow).Min();
        var report = new DeploymentReport
        {
            DeploymentName    = config.Name,
            ConfigurationId   = config.Id,
            Environment       = config.Environment,
            StartedAt         = started,
            CompletedAt       = DateTimeOffset.UtcNow,
            FinalStatus       = anyFailed ? DeploymentStatus.PartialFail : DeploymentStatus.Completed,
            Version           = config.WebLogicVersion,
            MiddlewareHome    = config.Paths.MiddlewareHome,
            DomainHome        = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName),
            AdminUrl          = $"http://{config.Network.Hostname}:{config.Domain.AdminPort}/console",
            Steps             = steps,
            AuditLog          = _log.GetEntries(LogLevel.Debug).ToList(),
            LocalPayload      = config.LocalPayload.UsedLocalRepository
                ? config.LocalPayload
                : null,
            JdkInstallation   = config.Java.LastInstallationDiagnostics
        };

        // ── Process lifecycle report ─────────────────────────────────────────
        if (_lifecycle is not null)
        {
            try
            {
                report.ProcessLifecycle = _lifecycle.GenerateSessionReport(config.Id);
                _log.Info(
                    $"Process lifecycle report generated: {report.ProcessLifecycle.Summary}",
                    "Finalization");
            }
            catch (Exception ex)
            {
                _log.Warning(
                    $"Could not generate process lifecycle report: {ex.Message}",
                    "Finalization");
            }
        }

        var stamp = $"{config.Id:N}";
        var html  = Path.Combine(config.Paths.ReportsDirectory, $"wedm-workflow-{stamp}.html");
        var json  = Path.Combine(config.Paths.ReportsDirectory, $"wedm-workflow-{stamp}.json");

        await _log.WriteHtmlReportAsync(report, html).ConfigureAwait(false);
        await _log.WriteJsonReportAsync(report, json).ConfigureAwait(false);

        sw.Stop();
        return StepExecutionResult.Ok($"Reports: {html}", sw.Elapsed);
    }
}
