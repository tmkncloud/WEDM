using System.Text.Json;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Opatch;

namespace WEDM.Engine.Workflow.Steps;

public sealed class ValidatePatchHostPrereqsStep : IStepExecutor
{
    private readonly IValidationEngine _validator;
    private readonly ILoggingService   _log;

    public ValidatePatchHostPrereqsStep(IValidationEngine validator, ILoggingService log)
    {
        _validator = validator;
        _log       = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r  = await _validator.ValidateForPatchingAsync(config, cancellationToken);
        sw.Stop();
        if (!r.CanProceed)
            return StepExecutionResult.Fail($"Patch host validation failed ({r.Fatals} fatal / {r.FailedCount} errors).", 40);
        _log.Info("Patch host validation passed.", "OPatch");
        return StepExecutionResult.Ok("Patch host validation passed.", sw.Elapsed);
    }
}

public sealed class ValidateOpatchEnvironmentStep : IStepExecutor
{
    private readonly OpatchRunner         _opatch;
    private readonly IPatchExecutionState _state;
    private readonly ILoggingService       _log;

    public ValidateOpatchEnvironmentStep(OpatchRunner opatch, IPatchExecutionState state, ILoggingService log)
    {
        _opatch = opatch;
        _state  = state;
        _log    = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _state.BeginSession(config.Id);
        var ver = await _opatch.VersionAsync(config, cancellationToken);
        sw.Stop();
        if (ver.ExitCode != 0)
            return StepExecutionResult.Fail($"OPatch version check failed: {ver.Errors}", ver.ExitCode);
        _state.OpatchVersionOutput = ver.Output;
        _log.Info($"OPatch version output captured ({ver.Output.Length} chars).", "OPatch");
        return StepExecutionResult.Ok("OPatch version OK.", sw.Elapsed);
    }
}

public sealed class ValidatePatchStagingStep : IStepExecutor
{
    private readonly IPatchExecutionState _state;
    private readonly ILoggingService      _log;

    public ValidatePatchStagingStep(IPatchExecutionState state, ILoggingService log)
    {
        _state = state;
        _log   = log;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, notes) = PatchStagingValidator.ValidateStagingTree(config.Patches.PatchStagingDirectory);
        _state.StagingNotes = notes;
        foreach (var n in notes) _log.Info(n, "OPatch.Staging");
        sw.Stop();
        if (!ok)
            return Task.FromResult(StepExecutionResult.Fail("Patch staging validation failed.", 41));
        return Task.FromResult(StepExecutionResult.Ok(string.Join("; ", notes), sw.Elapsed));
    }
}

public sealed class OpatchConflictCheckStep : IStepExecutor
{
    private readonly OpatchRunner     _opatch;
    private readonly ILoggingService  _log;

    public OpatchConflictCheckStep(OpatchRunner opatch, ILoggingService log)
    {
        _opatch = opatch;
        _log    = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Patches.RunConflictPrerequisites)
            return StepExecutionResult.Ok("Conflict prerequisites skipped by configuration.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var dir in PatchStagingValidator.EnumeratePatchDirectories(config.Patches.PatchStagingDirectory))
        {
            var r = await _opatch.PrereqConflictAsync(config, dir, cancellationToken);
            if (r.ExitCode != 0)
            {
                sw.Stop();
                return StepExecutionResult.Fail(
                    $"OPatch conflict prerequisite failed for '{dir}' (exit {r.ExitCode}): {r.Output} {r.Errors}",
                    r.ExitCode);
            }
        }

        sw.Stop();
        _log.Info("OPatch CheckConflictAgainstOHWithPatch prerequisites passed for all staged patches.", "OPatch");
        return StepExecutionResult.Ok("No patch conflicts reported by OPatch prereq.", sw.Elapsed);
    }
}

public sealed class PrePatchOpatchInventorySnapshotStep : IStepExecutor
{
    private readonly OpatchRunner         _opatch;
    private readonly IPatchExecutionState _state;
    private readonly ILoggingService      _log;

    public PrePatchOpatchInventorySnapshotStep(OpatchRunner opatch, IPatchExecutionState state, ILoggingService log)
    {
        _opatch = opatch;
        _state  = state;
        _log    = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Patches.CaptureInventorySnapshots)
            return StepExecutionResult.Ok("Inventory snapshot disabled.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(config.Paths.ReportsDirectory);
        var inv = await _opatch.LsinventoryAsync(config, cancellationToken);
        sw.Stop();
        if (inv.ExitCode != 0)
            return StepExecutionResult.Fail($"lsinventory failed: {inv.Errors}", inv.ExitCode);

        var path = Path.Combine(config.Paths.ReportsDirectory, $"opatch-lsinventory-pre-{config.Id:N}.txt");
        await File.WriteAllTextAsync(path, inv.Output, cancellationToken);
        _state.PreInventoryRaw       = inv.Output;
        _state.PreInventoryFilePath  = path;
        _state.ParsedPrePatches      = OpatchInventoryParser.Parse(inv.Output);
        _log.Info($"Pre-patch inventory saved: {path} ({_state.ParsedPrePatches.Count} patch rows parsed).", "OPatch");
        return StepExecutionResult.Ok(path, sw.Elapsed);
    }
}

public sealed class DetectBlockingMiddlewareProcessesStep : IStepExecutor
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService     _log;

    public DetectBlockingMiddlewareProcessesStep(IPowerShellExecutor ps, ILoggingService log)
    {
        _ps  = ps;
        _log = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Patches.CheckForRunningMiddlewareProcesses)
            return StepExecutionResult.Ok("Running-process check disabled.");

        const string body = @"
$hits = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
  $_.Name -ieq 'java.exe' -and $_.CommandLine -match 'weblogic|Dweblogic|nodemanager'
}
if ($hits) {
  $hits | ForEach-Object { Write-Warning ""Blocking: PID=$($_.ProcessId) $($_.CommandLine)"" }
  exit 2
}
exit 0
";
        var r = await _ps.ExecuteCommandAsync(body.Trim(), cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(2));
        if (r.ExitCode == 2)
            return StepExecutionResult.Fail(
                "Active WebLogic-related Java processes detected. Stop AdminServer, managed servers, and Node Manager before patching.",
                2);
        if (r.ExitCode != 0)
            return StepExecutionResult.Fail($"Process scan failed: {r.Errors}", r.ExitCode);
        _log.Info("No blocking WebLogic Java processes detected.", "OPatch");
        return StepExecutionResult.Ok("Process check passed.");
    }
}

public sealed class PrePatchMetadataSnapshotStep : IStepExecutor
{
    private readonly IPatchExecutionState _state;
    private readonly ILoggingService       _log;

    public PrePatchMetadataSnapshotStep(IPatchExecutionState state, ILoggingService log)
    {
        _state = state;
        _log   = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(config.Paths.SnapshotDirectory);
        var drive = Path.GetPathRoot(Path.GetFullPath(config.Paths.MiddlewareHome)) ?? "C:\\";
        long freeBytes = 0;
        try
        {
            foreach (var di in DriveInfo.GetDrives().Where(d => d.Name.Equals(drive, StringComparison.OrdinalIgnoreCase)))
                if (di.IsReady) freeBytes = di.AvailableFreeSpace;
        }
        catch { /* ignore */ }

        var meta = new
        {
            config.Id,
            config.Paths.MiddlewareHome,
            config.Paths.OracleInventory,
            config.Patches.PatchStagingDirectory,
            OpatchVersion = _state.OpatchVersionOutput,
            StagingNotes  = _state.StagingNotes,
            ParsedPrePatchCount = _state.ParsedPrePatches.Count,
            DiskFreeBytes = freeBytes,
            CapturedAt = DateTimeOffset.UtcNow
        };
        var path = Path.Combine(config.Paths.SnapshotDirectory, $"pre-patch-metadata-{config.Id:N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        _state.MetadataSnapshotPath = path;
        sw.Stop();
        _log.Info($"Pre-patch metadata snapshot: {path}", "OPatch");
        return StepExecutionResult.Ok(path, sw.Elapsed);
    }
}

public sealed class OpatchApplyPatchesStep : IStepExecutor
{
    private readonly OpatchRunner         _opatch;
    private readonly IPatchExecutionState _state;

    public OpatchApplyPatchesStep(OpatchRunner opatch, IPatchExecutionState state)
    {
        _opatch = opatch;
        _state  = state;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PowerShellResult r;
        if (config.Patches.UseNapply)
        {
            r = await _opatch.NapplySilentAsync(config, config.Patches.PatchStagingDirectory, cancellationToken);
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            var exit = 0;
            foreach (var dir in PatchStagingValidator.EnumeratePatchDirectories(config.Patches.PatchStagingDirectory))
            {
                r = await _opatch.ApplySilentAsync(config, dir, cancellationToken);
                sb.AppendLine($"--- apply {dir} exit {r.ExitCode} ---");
                sb.AppendLine(r.Output);
                if (!string.IsNullOrEmpty(r.Errors)) sb.AppendLine(r.Errors);
                exit = r.ExitCode;
                if (exit != 0) break;
            }

            sw.Stop();
            _state.LastApplyStdout   = sb.ToString();
            _state.LastApplyExitCode = exit;
            if (exit != 0)
                return StepExecutionResult.Fail($"Sequential OPatch apply failed (exit {exit}).", exit);
            return StepExecutionResult.Ok(sb.ToString(), sw.Elapsed);
        }

        sw.Stop();
        _state.LastApplyStdout   = r.Output + r.Errors;
        _state.LastApplyExitCode = r.ExitCode;
        if (r.TimedOut)
            return StepExecutionResult.Fail("OPatch apply timed out.", -2);
        if (r.ExitCode != 0)
            return StepExecutionResult.Fail($"OPatch apply failed (exit {r.ExitCode}): {r.Errors}", r.ExitCode);
        return StepExecutionResult.Ok(r.Output, sw.Elapsed);
    }
}

public sealed class OpatchPostApplyInventoryStep : IStepExecutor
{
    private readonly OpatchRunner         _opatch;
    private readonly IPatchExecutionState _state;
    private readonly ILoggingService      _log;

    public OpatchPostApplyInventoryStep(OpatchRunner opatch, IPatchExecutionState state, ILoggingService log)
    {
        _opatch = opatch;
        _state  = state;
        _log    = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Patches.CaptureInventorySnapshots)
            return StepExecutionResult.Ok("Post inventory skipped.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var inv = await _opatch.LsinventoryAsync(config, cancellationToken);
        sw.Stop();
        if (inv.ExitCode != 0)
            return StepExecutionResult.Fail($"Post-apply lsinventory failed: {inv.Errors}", inv.ExitCode);

        var path = Path.Combine(config.Paths.ReportsDirectory, $"opatch-lsinventory-post-{config.Id:N}.txt");
        await File.WriteAllTextAsync(path, inv.Output, cancellationToken);
        _state.PostInventoryRaw       = inv.Output;
        _state.PostInventoryFilePath  = path;
        _state.ParsedPostPatches      = OpatchInventoryParser.Parse(inv.Output);
        _log.Info($"Post-patch inventory: {path} ({_state.ParsedPostPatches.Count} rows parsed).", "OPatch");
        return StepExecutionResult.Ok(path, sw.Elapsed);
    }
}

public sealed class GeneratePatchComplianceReportStep : IStepExecutor
{
    private readonly IPatchExecutionState _state;
    private readonly IPatchReportWriter    _writer;
    private readonly ILoggingService       _log;

    public GeneratePatchComplianceReportStep(IPatchExecutionState state, IPatchReportWriter writer, ILoggingService log)
    {
        _state  = state;
        _writer = writer;
        _log    = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(config.Paths.ReportsDirectory);
        var stamp = $"{config.Id:N}";
        var report = new PatchReport
        {
            ConfigurationId           = config.Id,
            OracleHome                = config.Paths.MiddlewareHome,
            OpatchVersion             = _state.OpatchVersionOutput ?? string.Empty,
            StagingPath               = config.Patches.PatchStagingDirectory,
            PatchesBefore             = _state.ParsedPrePatches,
            PatchesAfter              = _state.ParsedPostPatches,
            StagingValidationNotes    = _state.StagingNotes,
            PreInventoryPath          = _state.PreInventoryFilePath ?? string.Empty,
            PostInventoryPath         = _state.PostInventoryFilePath ?? string.Empty,
            MetadataSnapshotPath      = _state.MetadataSnapshotPath ?? string.Empty,
            ApplySucceeded            = _state.LastApplyExitCode == 0,
            ApplyLogSummary           = _state.LastApplyStdout ?? string.Empty
        };

        var html = Path.Combine(config.Paths.ReportsDirectory, $"wedm-opatch-{stamp}.html");
        var json = Path.Combine(config.Paths.ReportsDirectory, $"wedm-opatch-{stamp}.json");
        await _writer.WriteHtmlAsync(report, html, cancellationToken).ConfigureAwait(false);
        await _writer.WriteJsonAsync(report, json, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        _log.Info($"Patch compliance reports written: {html}", "OPatch");
        return StepExecutionResult.Ok($"{html};{json}", sw.Elapsed);
    }
}
