using System.Text;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Opatch;

/// <summary>
/// Executes real Oracle OPatch CLI commands via PowerShell with ORACLE_HOME and optional inventory pointer.
/// </summary>
public sealed class OpatchRunner
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService     _log;

    public OpatchRunner(IPowerShellExecutor ps, ILoggingService log)
    {
        _ps  = ps;
        _log = log;
    }

    public async Task<PowerShellResult> RunAsync(
        DeploymentConfiguration config,
        IReadOnlyList<string> opatchArguments,
        CancellationToken cancellationToken = default)
    {
        var oh = Path.GetFullPath(config.Paths.MiddlewareHome);
        var opatchBat = OpatchPaths.ResolveOpatchBat(oh, config.Patches.OpatchBatPathOverride);
        if (opatchBat is null)
            return PowerShellResult.Fail("OPatch (opatch.bat) could not be resolved for this Oracle Home.");

        _ = OpatchPaths.WriteOraInstPointer(config);
        var timeout = TimeSpan.FromMinutes(Math.Max(15, config.Patches.OpatchTimeoutMinutes));

        var ohQ    = "'" + oh.Replace("'", "''", StringComparison.Ordinal) + "'";
        var batQ  = "'" + opatchBat.Replace("'", "''", StringComparison.Ordinal) + "'";

        var args = new StringBuilder();
        foreach (var a in opatchArguments)
        {
            if (args.Length > 0) args.Append(',');
            args.Append("'").Append(a.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        }

        var body = $@"
$ErrorActionPreference = 'Continue'
$env:ORACLE_HOME = {ohQ}
$env:PATH = ""$env:ORACLE_HOME\\OPatch;$env:ORACLE_HOME\\oracle_common\\OPatch;$env:PATH""
$opatch = {batQ}
$argList = @({args})
Write-Output (""OPatch command: $opatch "" + ($argList -join ' '))
$p = Start-Process -FilePath $opatch -ArgumentList $argList -Wait -PassThru -NoNewWindow
exit $(if ($null -eq $p) {{ 1 }} else {{ $p.ExitCode }})
";

        _log.Info($"OPatch: {opatchBat} {string.Join(' ', opatchArguments)}", "OPatch");
        return await _ps.ExecuteCommandAsync(
            body.Trim(),
            workingDirectory: oh,
            runAsAdministrator: false,
            cancellationToken: cancellationToken,
            operationTimeout: timeout);
    }

    public Task<PowerShellResult> VersionAsync(DeploymentConfiguration config, CancellationToken ct)
        => RunAsync(config, new[] { "version" }, ct);

    public Task<PowerShellResult> LsinventoryAsync(DeploymentConfiguration config, CancellationToken ct)
    {
        var inv = OpatchPaths.WriteOraInstPointer(config);
        return RunAsync(config, new[]
        {
            "lsinventory", "-all", "-oh", config.Paths.MiddlewareHome,
            "-invPtrLoc", inv
        }, ct);
    }

    public Task<PowerShellResult> ApplySilentAsync(DeploymentConfiguration config, string patchDirectory, CancellationToken ct)
    {
        var patchDir = Path.GetFullPath(patchDirectory);
        return RunAsync(config, new[]
        {
            "apply",
            "-silent",
            "-oh", config.Paths.MiddlewareHome,
            "-invPtrLoc", OpatchPaths.WriteOraInstPointer(config),
            patchDir
        }, ct);
    }

    public Task<PowerShellResult> NapplySilentAsync(DeploymentConfiguration config, string patchBaseDirectory, CancellationToken ct)
    {
        var p = Path.GetFullPath(patchBaseDirectory);
        return RunAsync(config, new[]
        {
            "napply",
            "-silent",
            "-oh", config.Paths.MiddlewareHome,
            "-invPtrLoc", OpatchPaths.WriteOraInstPointer(config),
            p
        }, ct);
    }

    public Task<PowerShellResult> PrereqConflictAsync(DeploymentConfiguration config, string patchDirectory, CancellationToken ct)
    {
        var p = Path.GetFullPath(patchDirectory);
        return RunAsync(config, new[]
        {
            "prereq", "CheckConflictAgainstOHWithPatch",
            "-ph", p,
            "-oh", config.Paths.MiddlewareHome
        }, ct);
    }
}
