using System.Diagnostics;
using System.Text;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Automation;
using WEDM.Infrastructure.Security;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>Starts AdminServer (or its Windows service) so WLST online and nmEnroll can run.</summary>
public sealed class StartAdminServerForOnlineAutomationStep : IStepExecutor
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService     _log;

    public StartAdminServerForOnlineAutomationStep(IPowerShellExecutor ps, ILoggingService log)
    {
        _ps  = ps;
        _log = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.DomainOnlineAutomation.Enabled || !config.DomainOnlineAutomation.StartAdminServerIfNotRunning)
            return StepExecutionResult.Ok("Admin auto-start for online WLST skipped by configuration.");

        if (string.IsNullOrEmpty(config.Domain.AdminPassword))
            return StepExecutionResult.Fail("Admin password is required to start AdminServer for online automation.");

        var domainHome = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);
        var startCmd   = Path.Combine(domainHome, "startWebLogic.cmd");
        if (!File.Exists(startCmd))
            return StepExecutionResult.Fail($"startWebLogic.cmd not found at '{startCmd}'.");

        var host = string.IsNullOrWhiteSpace(config.Network.Hostname) ? "127.0.0.1" : config.Network.Hostname;
        var port = config.Domain.AdminPort;
        var poll = Math.Clamp(config.DomainOnlineAutomation.AdminStartupPollSeconds, 3, 60);
        var mins = Math.Clamp(config.DomainOnlineAutomation.TimeoutMinutes, 5, 120);

        var hostQ = "'" + host.Replace("'", "''", StringComparison.Ordinal) + "'";
        var scQ   = "'" + startCmd.Replace("'", "''", StringComparison.Ordinal) + "'";

        var body = $@"
$ErrorActionPreference = 'Continue'
if (Test-NetConnection -ComputerName {hostQ} -Port {port} -InformationLevel Quiet -WarningAction SilentlyContinue) {{
  Write-Output 'Admin port already listening.'
  exit 0
}}
Write-Output 'Starting AdminServer via startWebLogic.cmd ...'
$null = Start-Process -FilePath {scQ} -WorkingDirectory (Split-Path {scQ}) -WindowStyle Hidden
$deadline = (Get-Date).AddMinutes({mins})
while ((Get-Date) -lt $deadline) {{
  if (Test-NetConnection -ComputerName {hostQ} -Port {port} -InformationLevel Quiet -WarningAction SilentlyContinue) {{
    Write-Output 'Admin port is up.'
    exit 0
  }}
  Start-Sleep -Seconds {poll}
}}
exit 3
";

        var sw = Stopwatch.StartNew();
        var r  = await _ps.ExecuteCommandAsync(
            body.Trim(),
            workingDirectory: domainHome,
            runAsAdministrator: false,
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(mins + 2));

        sw.Stop();
        if (r.ExitCode == 0)
        {
            _log.Info("AdminServer listen port is available for online WLST.", "DomainOnline");
            return StepExecutionResult.Ok(r.Output, sw.Elapsed);
        }

        return StepExecutionResult.Fail(
            $"AdminServer did not become reachable on {host}:{port} within timeout. Output: {r.Output} {r.Errors}",
            r.ExitCode);
    }
}

/// <summary>Runs WLST online: optional nmEnroll, production mode, machine mapping for Admin and managed servers.</summary>
public sealed class WlstOnlinePostBootAutomationStep : IStepExecutor
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService     _log;

    public WlstOnlinePostBootAutomationStep(IPowerShellExecutor ps, ILoggingService log)
    {
        _ps  = ps;
        _log = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.DomainOnlineAutomation.Enabled)
            return StepExecutionResult.Ok("Online WLST automation disabled.");

        if (string.IsNullOrEmpty(config.Domain.AdminPassword))
            return StepExecutionResult.Fail("Admin password is required for WLST online connect.");

        var wlst = WlstDomainScriptBuilder.ResolveWlstCmd(config);
        if (!File.Exists(wlst))
            return StepExecutionResult.Fail($"WLST not found at '{wlst}'.");

        var py = WlstOnlineScriptBuilder.BuildPostBootOnlinePy(config);
        var pyPath = Path.Combine(config.Paths.TempDirectory, $"wedm_online_wlst_{config.Id:N}.py");
        Directory.CreateDirectory(config.Paths.TempDirectory);
        await File.WriteAllTextAsync(pyPath, py, cancellationToken);
        _log.Info($"Online WLST script written: {pyPath}", "DomainOnline");

        var host = string.IsNullOrWhiteSpace(config.Network.Hostname) ? "127.0.0.1" : config.Network.Hostname;
        var url  = $"t3://{host}:{config.Domain.AdminPort}";
        var pwdB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Domain.AdminPassword));
        var userEsc = config.Domain.AdminUsername.Replace("'", "''", StringComparison.Ordinal);
        var urlEsc  = url.Replace("'", "''", StringComparison.Ordinal);
        var wlstQ   = "'" + wlst.Replace("'", "''", StringComparison.Ordinal) + "'";
        var pyQ     = "'" + pyPath.Replace("'", "''", StringComparison.Ordinal) + "'";

        var nmFlag = config.DomainOnlineAutomation.RunNmEnroll ? "1" : "0";
        var hdFlag = config.DomainOnlineAutomation.ApplyOnlineProductionAndMachineMapping ? "1" : "0";

        var body = $@"
$ErrorActionPreference = 'Continue'
$env:WEDM_ADMIN_USER = '{userEsc}'
$env:WEDM_ADMIN_PASS = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{pwdB64}'))
$env:WEDM_ADMIN_URL = '{urlEsc}'
$env:WEDM_RUN_NM_ENROLL = '{nmFlag}'
$env:WEDM_APPLY_ONLINE_HARDENING = '{hdFlag}'
Write-Output ('[WEDM] WLST online: ' + {wlstQ} + ' ' + {pyQ})
$p = Start-Process -FilePath {wlstQ} -ArgumentList @({pyQ}) -Wait -PassThru -NoNewWindow
exit $(if ($null -eq $p) {{ 1 }} else {{ $p.ExitCode }})
";

        var sw = Stopwatch.StartNew();
        var mins = Math.Clamp(config.DomainOnlineAutomation.TimeoutMinutes, 5, 180);
        var r = await _ps.ExecuteCommandAsync(
            body.Trim(),
            workingDirectory: config.Paths.TempDirectory,
            runAsAdministrator: false,
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(mins));

        sw.Stop();
        _log.Info(SecretRedactor.Redact(r.Output), "DomainOnline.WLST");
        if (!string.IsNullOrEmpty(r.Errors))
            _log.Warning(SecretRedactor.Redact(r.Errors), "DomainOnline.WLST");

        if (r.TimedOut)
            return StepExecutionResult.Fail("WLST online automation timed out.", -2);
        if (r.ExitCode != 0)
            return StepExecutionResult.Fail(
                $"WLST online failed (exit {r.ExitCode}): {SecretRedactor.Redact(r.Output + " " + r.Errors)}",
                r.ExitCode);

        return StepExecutionResult.Ok(SecretRedactor.Redact(r.Output), sw.Elapsed);
    }
}

/// <summary>Validates Node Manager TCP reachability on the configured listen address.</summary>
public sealed class ValidateNodeManagerReachabilityStep : IStepExecutor
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService     _log;

    public ValidateNodeManagerReachabilityStep(IPowerShellExecutor ps, ILoggingService log)
    {
        _ps  = ps;
        _log = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.DomainOnlineAutomation.Enabled)
            return StepExecutionResult.Ok("Node Manager reachability check skipped.");

        var host = string.IsNullOrWhiteSpace(config.Domain.NodeManager.ListenAddress)
            ? "127.0.0.1"
            : config.Domain.NodeManager.ListenAddress;
        if (host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
            host = "127.0.0.1";

        var hostQ = "'" + host.Replace("'", "''", StringComparison.Ordinal) + "'";
        var port  = config.Domain.NodeManager.Port;
        var body  = $@"
$t = Test-NetConnection -ComputerName {hostQ} -Port {port} -InformationLevel Quiet -WarningAction SilentlyContinue
if ($t) {{ exit 0 }} else {{ exit 1 }}
";

        var r = await _ps.ExecuteCommandAsync(
            body.Trim(),
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(2));

        if (r.ExitCode != 0)
        {
            _log.Warning($"Node Manager not reachable at {host}:{port} (NM may not be started yet).", "DomainOnline");
            if (config.DomainHardening.StrictPostValidation)
                return StepExecutionResult.Fail(
                    $"Strict validation: Node Manager TCP check failed for {host}:{port}. Start installNodeMgrSvc.cmd or startNodeManager.cmd before enforcing this check.",
                    55);
            return StepExecutionResult.Ok(
                $"Node Manager not listening on {host}:{port} — continuing (non-strict profile). {r.Output}");
        }

        _log.Info($"Node Manager TCP check OK: {host}:{port}", "DomainOnline");
        return StepExecutionResult.Ok($"Node Manager reachable at {host}:{port}.");
    }
}
