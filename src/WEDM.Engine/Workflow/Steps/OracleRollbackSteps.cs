using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Workflow.Steps;

// ═══════════════════════════════════════════════════════════════════════════════
// Oracle-Aware Rollback Executors
// ═══════════════════════════════════════════════════════════════════════════════
//
// These executors supersede the basic Remove-MiddlewareHome / Remove-FormsReports
// / Remove-OHS / Remove-JavaEnvVars executors with full Oracle-aware logic:
//
//   Phase 1  — Stop Oracle/JVM middleware processes (OUI orphans, managed servers)
//   Phase 2  — Stop and remove Windows services registered by this step
//   Phase 3  — Remove machine-level environment variables set by this step
//   Phase 4  — Remove registry keys created by this step
//   Phase 5  — Detach Oracle home(s) from Central Inventory (XML mutation)
//   Phase 6  — Delete Oracle home directory tree(s)
//   Phase 7  — Delete WEDM-generated files (response files, silent XML, etc.)
//   Phase 8  — Accumulate results in config.OracleRollback report
//
// Data source: step.RollbackCompensation (populated on step success).
// Fallback:    config-derived paths when the compensation record is absent.
// Dry-run:     config.OracleLifecycle.DryRunRollback — logs all operations but
//              skips every destructive action (process kill, fs delete, inv mutate).
// ═══════════════════════════════════════════════════════════════════════════════

// ── OracleInstallRollbackExecutor ─────────────────────────────────────────────

/// <summary>
/// Oracle-aware rollback executor for <b>InstallInfrastructure</b> and
/// <b>InstallWebLogic</b> steps — both use the <c>"Remove-MiddlewareHome"</c>
/// rollback action.
///
/// Full rollback sequence:
///   1. Stop Oracle middleware processes
///   2. Stop and delete registered Windows services
///   3. Remove machine environment variables
///   4. Remove registry keys
///   5. Detach Oracle home from Central Inventory
///   6. Delete middleware home directory
///   7. Delete WEDM-generated files
///   8. Accumulate into <see cref="OracleRollbackReport"/>
/// </summary>
public sealed class OracleInstallRollbackExecutor : IStepExecutor
{
    private readonly ILoggingService         _log;
    private readonly IOracleInventoryService _inventory;
    private readonly IOracleProcessManager   _processManager;
    private readonly OracleRollbackCore      _core;

    public OracleInstallRollbackExecutor(
        ILoggingService         log,
        IOracleInventoryService inventory,
        IOracleProcessManager   processManager)
    {
        _log            = log;
        _inventory      = inventory;
        _processManager = processManager;
        _core           = new OracleRollbackCore(log, inventory, processManager);
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep         step,
        DeploymentConfiguration config,
        CancellationToken      cancellationToken = default)
    {
        const string tag      = "Rollback-OracleInstall";
        var          dryRun   = config.OracleLifecycle.DryRunRollback;
        var          comp     = step.RollbackCompensation;
        var          report   = new OracleRollbackReport { DryRunMode = dryRun };
        var          messages = new List<string>();

        _log.Info(
            $"{tag}: Starting Oracle install rollback for step '{step.Name}'" +
            (dryRun ? " [DRY-RUN — no destructive actions]" : "") + ".",
            "Rollback");

        // Resolve home paths — prefer compensation data, fall back to config
        var homePaths = comp?.OracleHomePaths.Count > 0
            ? comp.OracleHomePaths
            : [config.Paths.MiddlewareHome];

        var inventoryPath = comp?.OracleInventoryPath
            ?? config.Paths.OracleInventory;

        try
        {
            // ── Phase 1: Stop Oracle processes ────────────────────────────────
            var processResult = await _core.StopOracleProcessesAsync(
                config, dryRun, tag, cancellationToken);
            messages.AddRange(processResult.Messages);
            report.StoppedProcesses.AddRange(processResult.StoppedDescriptions);

            // ── Phase 2: Stop and remove Windows services ─────────────────────
            var serviceNames = comp?.CreatedServiceNames.Count > 0
                ? comp.CreatedServiceNames
                : OracleRollbackCore.InferMiddlewareServiceNames(config);

            foreach (var svc in serviceNames)
            {
                var svcResult = await _core.StopAndRemoveServiceAsync(svc, dryRun, tag, cancellationToken);
                if (svcResult.Removed)
                    report.StoppedAndRemovedServices.Add(svc);
                else if (svcResult.Warning is not null)
                    report.RemainingWarnings.Add(svcResult.Warning);
                messages.Add(svcResult.Message);
            }

            // ── Phase 3: Remove environment variables ─────────────────────────
            var envVarNames = comp?.SetEnvironmentVariableNames.Count > 0
                ? comp.SetEnvironmentVariableNames
                : OracleRollbackCore.InferOracleEnvVarNames();

            foreach (var varName in envVarNames)
            {
                var envResult = _core.RemoveEnvironmentVariable(varName, dryRun, tag);
                if (envResult.Removed)
                    report.RemovedEnvironmentVariables.Add(varName);
                messages.Add(envResult.Message);
            }

            // ── Phase 4: Remove registry keys ─────────────────────────────────
            var registryKeys = comp?.CreatedRegistryKeyPaths ?? [];
            foreach (var keyPath in registryKeys)
            {
                var regResult = _core.RemoveRegistryKey(keyPath, dryRun, tag);
                messages.Add(regResult.Message);
            }

            // ── Phase 5: Detach from Oracle Central Inventory ─────────────────
            foreach (var homePath in homePaths)
            {
                var invResult = _core.DetachFromInventory(homePath, inventoryPath, dryRun, tag);
                if (invResult.Detached)
                    report.DetachedInventoryEntries.Add($"{invResult.HomeName} @ {homePath}");
                messages.Add(invResult.Message);
            }

            // ── Phase 6: Delete Oracle home directories ────────────────────────
            foreach (var homePath in homePaths)
            {
                var fsResult = _core.DeleteDirectory(homePath, dryRun, tag);
                if (fsResult.Deleted)
                    report.RemovedHomes.Add(homePath);
                else if (fsResult.Warning is not null)
                    report.RemainingWarnings.Add(fsResult.Warning);
                messages.Add(fsResult.Message);
            }

            // ── Phase 7: Delete generated files ───────────────────────────────
            var generatedFiles = comp?.GeneratedFilePaths ?? [];
            foreach (var filePath in generatedFiles)
            {
                var fileResult = _core.DeleteFile(filePath, dryRun, tag);
                if (fileResult.Deleted)
                    report.RemovedGeneratedFiles.Add(filePath);
                messages.Add(fileResult.Message);
            }

            // ── Phase 8: Run post-rollback verification ────────────────────────
            var verification = OracleRollbackVerificationService.Verify(
                homePaths, inventoryPath, _inventory, _log, tag);

            report.InventoryClean    = verification.IsClean;
            report.NoOuiLocks        = !OracleRollbackCore.HasInventoryLocks(inventoryPath);
            report.NoOrphanProcesses = _processManager.DetectMiddlewareProcesses().Count == 0;
            report.RemainingWarnings.AddRange(verification.RemainingWarnings);
            report.ManualActionsRequired.AddRange(verification.ManualActionsRequired);

            // ── Accumulate in config report ────────────────────────────────────
            AccumulateReport(config, report);

            var summary = $"Oracle install rollback complete: " +
                          $"{report.RemovedHomes.Count} home(s) removed, " +
                          $"{report.DetachedInventoryEntries.Count} inventory entry/ies detached, " +
                          $"{report.StoppedAndRemovedServices.Count} service(s) removed" +
                          (dryRun ? " [DRY-RUN]" : "") + ".";

            _log.Info($"{tag}: {summary}", "Rollback");

            // Surface any manual actions as a soft failure
            if (report.ManualActionsRequired.Count > 0)
            {
                var manualNote = "Manual actions required: " +
                    string.Join("; ", report.ManualActionsRequired);
                return StepExecutionResult.OkWithManualFollowUp(
                    string.Join("\n", messages.Append(manualNote)));
            }

            return StepExecutionResult.Ok(string.Join("\n", messages));
        }
        catch (Exception ex)
        {
            _log.Error($"{tag}: Unexpected exception during Oracle install rollback.", ex, "Rollback");
            AccumulateReport(config, report);
            return StepExecutionResult.Fail(
                $"Oracle install rollback failed unexpectedly: {ex.Message}", 1, ex);
        }
    }

    private static void AccumulateReport(DeploymentConfiguration config, OracleRollbackReport partial)
    {
        if (config.OracleRollback is null)
            config.OracleRollback = partial;
        else
            config.OracleRollback.MergeFrom(partial);
    }
}

// ── OracleFormsReportsRollbackExecutor ───────────────────────────────────────

/// <summary>
/// Oracle-aware rollback executor for <b>InstallFormsReports</b> steps —
/// rollback action <c>"Remove-FormsReports"</c>.
///
/// Performs the same full Oracle rollback sequence as
/// <see cref="OracleInstallRollbackExecutor"/> but targeted at the
/// Forms/Reports Oracle home and its associated services.
/// </summary>
public sealed class OracleFormsReportsRollbackExecutor : IStepExecutor
{
    private readonly ILoggingService         _log;
    private readonly IOracleInventoryService _inventory;
    private readonly IOracleProcessManager   _processManager;
    private readonly OracleRollbackCore      _core;

    public OracleFormsReportsRollbackExecutor(
        ILoggingService         log,
        IOracleInventoryService inventory,
        IOracleProcessManager   processManager)
    {
        _log            = log;
        _inventory      = inventory;
        _processManager = processManager;
        _core           = new OracleRollbackCore(log, inventory, processManager);
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep          step,
        DeploymentConfiguration config,
        CancellationToken       cancellationToken = default)
    {
        const string tag    = "Rollback-FormsReports";
        var dryRun          = config.OracleLifecycle.DryRunRollback;
        var comp            = step.RollbackCompensation;
        var report          = new OracleRollbackReport { DryRunMode = dryRun };
        var messages        = new List<string>();

        _log.Info(
            $"{tag}: Starting Forms/Reports rollback" +
            (dryRun ? " [DRY-RUN]" : "") + ".",
            "Rollback");

        // Resolve paths from compensation or config
        var homePaths = comp?.OracleHomePaths.Count > 0
            ? comp.OracleHomePaths
            : ResolveFormsHomePaths(config);

        var inventoryPath = comp?.OracleInventoryPath ?? config.Paths.OracleInventory;

        try
        {
            // Phase 1: Stop processes
            var processResult = await _core.StopOracleProcessesAsync(
                config, dryRun, tag, cancellationToken);
            messages.AddRange(processResult.Messages);
            report.StoppedProcesses.AddRange(processResult.StoppedDescriptions);

            // Phase 2: Services (OHS component services for Forms)
            var serviceNames = comp?.CreatedServiceNames.Count > 0
                ? comp.CreatedServiceNames
                : OracleRollbackCore.InferFormsServicesNames(config);

            foreach (var svc in serviceNames)
            {
                var svcResult = await _core.StopAndRemoveServiceAsync(svc, dryRun, tag, cancellationToken);
                if (svcResult.Removed) report.StoppedAndRemovedServices.Add(svc);
                else if (svcResult.Warning is not null) report.RemainingWarnings.Add(svcResult.Warning);
                messages.Add(svcResult.Message);
            }

            // Phase 3: Environment variables
            var envVarNames = comp?.SetEnvironmentVariableNames ?? [];
            foreach (var varName in envVarNames)
            {
                var envResult = _core.RemoveEnvironmentVariable(varName, dryRun, tag);
                if (envResult.Removed) report.RemovedEnvironmentVariables.Add(varName);
                messages.Add(envResult.Message);
            }

            // Phase 4: Registry keys
            var registryKeys = comp?.CreatedRegistryKeyPaths ?? [];
            foreach (var keyPath in registryKeys)
                messages.Add(_core.RemoveRegistryKey(keyPath, dryRun, tag).Message);

            // Phase 5: Detach from inventory
            foreach (var homePath in homePaths)
            {
                var invResult = _core.DetachFromInventory(homePath, inventoryPath, dryRun, tag);
                if (invResult.Detached)
                    report.DetachedInventoryEntries.Add($"{invResult.HomeName} @ {homePath}");
                messages.Add(invResult.Message);
            }

            // Phase 6: Delete home directories
            foreach (var homePath in homePaths)
            {
                var fsResult = _core.DeleteDirectory(homePath, dryRun, tag);
                if (fsResult.Deleted) report.RemovedHomes.Add(homePath);
                else if (fsResult.Warning is not null) report.RemainingWarnings.Add(fsResult.Warning);
                messages.Add(fsResult.Message);
            }

            // Phase 7: Generated files
            var generatedFiles = comp?.GeneratedFilePaths ?? [];
            foreach (var filePath in generatedFiles)
            {
                var fileResult = _core.DeleteFile(filePath, dryRun, tag);
                if (fileResult.Deleted) report.RemovedGeneratedFiles.Add(filePath);
                messages.Add(fileResult.Message);
            }

            // Phase 8: Verification
            var verification = OracleRollbackVerificationService.Verify(
                homePaths, inventoryPath, _inventory, _log, tag);

            report.InventoryClean    = verification.IsClean;
            report.NoOuiLocks        = !OracleRollbackCore.HasInventoryLocks(inventoryPath);
            report.NoOrphanProcesses = _processManager.DetectMiddlewareProcesses().Count == 0;
            report.RemainingWarnings.AddRange(verification.RemainingWarnings);
            report.ManualActionsRequired.AddRange(verification.ManualActionsRequired);

            AccumulateReport(config, report);

            _log.Info(
                $"{tag}: Forms/Reports rollback complete" +
                (dryRun ? " [DRY-RUN]" : "") + ".",
                "Rollback");

            if (report.ManualActionsRequired.Count > 0)
                return StepExecutionResult.OkWithManualFollowUp(
                    string.Join("\n", messages.Append(
                        "Manual actions required: " +
                        string.Join("; ", report.ManualActionsRequired))));

            return StepExecutionResult.Ok(string.Join("\n", messages));
        }
        catch (Exception ex)
        {
            _log.Error($"{tag}: Unexpected exception.", ex, "Rollback");
            AccumulateReport(config, report);
            return StepExecutionResult.Fail(
                $"Forms/Reports rollback failed: {ex.Message}", 1, ex);
        }
    }

    private static List<string> ResolveFormsHomePaths(DeploymentConfiguration config)
    {
        var paths = new List<string>();
        var formsPath = config.Domain.FormsReports.FormsPath;
        if (!string.IsNullOrWhiteSpace(formsPath))
            paths.Add(formsPath);
        return paths;
    }

    private static void AccumulateReport(DeploymentConfiguration config, OracleRollbackReport partial)
    {
        if (config.OracleRollback is null) config.OracleRollback = partial;
        else config.OracleRollback.MergeFrom(partial);
    }
}

// ── OracleOhsWebTierRollbackExecutor ─────────────────────────────────────────

/// <summary>
/// Oracle-aware rollback executor for <b>InstallOHSWebTier</b> steps —
/// rollback action <c>"Remove-OHS"</c>.
///
/// Stops the OHS component, removes its Windows service entry, detaches
/// its Oracle home from the Central Inventory, and deletes its directory tree.
/// </summary>
public sealed class OracleOhsWebTierRollbackExecutor : IStepExecutor
{
    private readonly ILoggingService         _log;
    private readonly IOracleInventoryService _inventory;
    private readonly IOracleProcessManager   _processManager;
    private readonly OracleRollbackCore      _core;

    public OracleOhsWebTierRollbackExecutor(
        ILoggingService         log,
        IOracleInventoryService inventory,
        IOracleProcessManager   processManager)
    {
        _log            = log;
        _inventory      = inventory;
        _processManager = processManager;
        _core           = new OracleRollbackCore(log, inventory, processManager);
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep          step,
        DeploymentConfiguration config,
        CancellationToken       cancellationToken = default)
    {
        const string tag = "Rollback-OHSWebTier";
        var dryRun       = config.OracleLifecycle.DryRunRollback;
        var comp         = step.RollbackCompensation;
        var report       = new OracleRollbackReport { DryRunMode = dryRun };
        var messages     = new List<string>();

        _log.Info(
            $"{tag}: Starting OHS/WebTier rollback" +
            (dryRun ? " [DRY-RUN]" : "") + ".",
            "Rollback");

        var homePaths = comp?.OracleHomePaths.Count > 0
            ? comp.OracleHomePaths
            : ResolveOhsHomePaths(config);

        var inventoryPath = comp?.OracleInventoryPath ?? config.Paths.OracleInventory;

        try
        {
            // Phase 1: Stop processes
            var processResult = await _core.StopOracleProcessesAsync(
                config, dryRun, tag, cancellationToken);
            messages.AddRange(processResult.Messages);
            report.StoppedProcesses.AddRange(processResult.StoppedDescriptions);

            // Phase 2: Services
            var serviceNames = comp?.CreatedServiceNames.Count > 0
                ? comp.CreatedServiceNames
                : OracleRollbackCore.InferOhsServiceNames(config);

            foreach (var svc in serviceNames)
            {
                var svcResult = await _core.StopAndRemoveServiceAsync(svc, dryRun, tag, cancellationToken);
                if (svcResult.Removed) report.StoppedAndRemovedServices.Add(svc);
                else if (svcResult.Warning is not null) report.RemainingWarnings.Add(svcResult.Warning);
                messages.Add(svcResult.Message);
            }

            // Phase 3: Environment variables
            foreach (var varName in comp?.SetEnvironmentVariableNames ?? [])
            {
                var envResult = _core.RemoveEnvironmentVariable(varName, dryRun, tag);
                if (envResult.Removed) report.RemovedEnvironmentVariables.Add(varName);
                messages.Add(envResult.Message);
            }

            // Phase 4: Registry keys
            foreach (var keyPath in comp?.CreatedRegistryKeyPaths ?? [])
                messages.Add(_core.RemoveRegistryKey(keyPath, dryRun, tag).Message);

            // Phase 5: Detach from inventory
            foreach (var homePath in homePaths)
            {
                var invResult = _core.DetachFromInventory(homePath, inventoryPath, dryRun, tag);
                if (invResult.Detached)
                    report.DetachedInventoryEntries.Add($"{invResult.HomeName} @ {homePath}");
                messages.Add(invResult.Message);
            }

            // Phase 6: Delete home directories
            foreach (var homePath in homePaths)
            {
                var fsResult = _core.DeleteDirectory(homePath, dryRun, tag);
                if (fsResult.Deleted) report.RemovedHomes.Add(homePath);
                else if (fsResult.Warning is not null) report.RemainingWarnings.Add(fsResult.Warning);
                messages.Add(fsResult.Message);
            }

            // Phase 7: Generated files
            foreach (var filePath in comp?.GeneratedFilePaths ?? [])
            {
                var fileResult = _core.DeleteFile(filePath, dryRun, tag);
                if (fileResult.Deleted) report.RemovedGeneratedFiles.Add(filePath);
                messages.Add(fileResult.Message);
            }

            // Phase 8: Verification
            var verification = OracleRollbackVerificationService.Verify(
                homePaths, inventoryPath, _inventory, _log, tag);

            report.InventoryClean    = verification.IsClean;
            report.NoOuiLocks        = !OracleRollbackCore.HasInventoryLocks(inventoryPath);
            report.NoOrphanProcesses = _processManager.DetectMiddlewareProcesses().Count == 0;
            report.RemainingWarnings.AddRange(verification.RemainingWarnings);
            report.ManualActionsRequired.AddRange(verification.ManualActionsRequired);

            AccumulateReport(config, report);

            _log.Info(
                $"{tag}: OHS/WebTier rollback complete" +
                (dryRun ? " [DRY-RUN]" : "") + ".",
                "Rollback");

            if (report.ManualActionsRequired.Count > 0)
                return StepExecutionResult.OkWithManualFollowUp(
                    string.Join("\n", messages.Append(
                        "Manual actions required: " +
                        string.Join("; ", report.ManualActionsRequired))));

            return StepExecutionResult.Ok(string.Join("\n", messages));
        }
        catch (Exception ex)
        {
            _log.Error($"{tag}: Unexpected exception.", ex, "Rollback");
            AccumulateReport(config, report);
            return StepExecutionResult.Fail(
                $"OHS/WebTier rollback failed: {ex.Message}", 1, ex);
        }
    }

    private static List<string> ResolveOhsHomePaths(DeploymentConfiguration config)
    {
        // OHS home is typically MW_HOME/ohs1 or a sibling of the middleware home
        // We can't recover this without compensation — return empty list
        return [];
    }

    private static void AccumulateReport(DeploymentConfiguration config, OracleRollbackReport partial)
    {
        if (config.OracleRollback is null) config.OracleRollback = partial;
        else config.OracleRollback.MergeFrom(partial);
    }
}

// ── OracleJavaHomeRollbackExecutor ───────────────────────────────────────────

/// <summary>
/// Oracle-aware rollback executor for <b>ConfigureJavaHome</b> steps —
/// rollback action <c>"Remove-JavaEnvVars"</c>.
///
/// Removes JAVA_HOME and its bin directory from the machine PATH.
/// Captures the action in the Oracle rollback report for operator transparency.
/// Does NOT delete the JDK directory (handled by RemoveJdkStep).
/// </summary>
public sealed class OracleJavaHomeRollbackExecutor : IStepExecutor
{
    private readonly ILoggingService _log;

    public OracleJavaHomeRollbackExecutor(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep          step,
        DeploymentConfiguration config,
        CancellationToken       cancellationToken = default)
    {
        const string tag = "Rollback-JavaHome";
        var dryRun       = config.OracleLifecycle.DryRunRollback;
        var comp         = step.RollbackCompensation;
        var report       = new OracleRollbackReport { DryRunMode = dryRun };
        var messages     = new List<string>();

        _log.Info(
            $"{tag}: Starting JavaHome rollback" +
            (dryRun ? " [DRY-RUN]" : "") + ".",
            "Rollback");

        var javaHome = config.Java.JavaHome;

        // Determine which env vars to remove — prefer compensation list
        var envVarNames = comp?.SetEnvironmentVariableNames.Count > 0
            ? comp.SetEnvironmentVariableNames
            : ["JAVA_HOME"];

        try
        {
            foreach (var varName in envVarNames)
            {
                try
                {
                    var current = Environment.GetEnvironmentVariable(
                        varName, EnvironmentVariableTarget.Machine);

                    if (current is null)
                    {
                        var msg = $"{tag}: {varName} not set in machine environment — skipped.";
                        _log.Info(msg, "Rollback");
                        messages.Add(msg);
                        continue;
                    }

                    // Only remove if the value still matches our configured value
                    bool valueMatches = varName.Equals("JAVA_HOME", StringComparison.OrdinalIgnoreCase)
                        ? current.TrimEnd('\\').Equals(
                            javaHome.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                        : true; // For non-JAVA_HOME vars, trust the compensation record

                    if (!valueMatches)
                    {
                        var msg = $"{tag}: {varName} value ('{current}') does not match configured JavaHome — skipped.";
                        _log.Info(msg, "Rollback");
                        messages.Add(msg);
                        continue;
                    }

                    if (dryRun)
                    {
                        var msg = $"[DRY-RUN] Would remove machine env var {varName}='{current}'.";
                        _log.Info($"{tag}: {msg}", "Rollback");
                        messages.Add(msg);
                        report.RemovedEnvironmentVariables.Add($"{varName} [dry-run]");
                    }
                    else
                    {
                        Environment.SetEnvironmentVariable(varName, null, EnvironmentVariableTarget.Machine);
                        _log.Info($"{tag}: Removed machine env var {varName}.", "Rollback");
                        messages.Add($"Machine env var {varName} removed.");
                        report.RemovedEnvironmentVariables.Add(varName);
                    }
                }
                catch (UnauthorizedAccessException uae)
                {
                    var msg = $"Cannot remove {varName} — Administrator privileges required.";
                    _log.Error($"{tag}: {msg}", uae, "Rollback");
                    report.ManualActionsRequired.Add(
                        $"Remove machine env var '{varName}' manually via System Properties → Environment Variables.");
                    messages.Add($"WARNING: {msg}");
                }
            }

            // Remove JAVA_HOME\bin from machine PATH
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                var javaBin = Path.Combine(javaHome, "bin");
                try
                {
                    var machinePath = Environment.GetEnvironmentVariable(
                        "PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;

                    var segments = machinePath.Split(';')
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();

                    var filtered = segments
                        .Where(s => !s.TrimEnd('\\').Equals(
                            javaBin.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (filtered.Count < segments.Count)
                    {
                        if (dryRun)
                        {
                            messages.Add($"[DRY-RUN] Would remove '{javaBin}' from machine PATH.");
                        }
                        else
                        {
                            Environment.SetEnvironmentVariable(
                                "PATH", string.Join(";", filtered), EnvironmentVariableTarget.Machine);
                            _log.Info($"{tag}: Removed '{javaBin}' from machine PATH.", "Rollback");
                            messages.Add($"'{javaBin}' removed from machine PATH.");
                        }
                    }
                    else
                    {
                        messages.Add($"'{javaBin}' not found in machine PATH — already absent.");
                    }
                }
                catch (UnauthorizedAccessException uae)
                {
                    _log.Error($"{tag}: Cannot modify system PATH.", uae, "Rollback");
                    report.ManualActionsRequired.Add(
                        $"Remove '{javaBin}' from machine PATH manually via System Properties → Environment Variables.");
                    messages.Add($"WARNING: Cannot modify system PATH — Administrator privileges required.");
                }
            }

            // Accumulate report
            if (config.OracleRollback is null) config.OracleRollback = report;
            else config.OracleRollback.MergeFrom(report);

            _log.Info(
                $"{tag}: JavaHome rollback complete" +
                (dryRun ? " [DRY-RUN]" : "") + ".",
                "Rollback");

            if (report.ManualActionsRequired.Count > 0)
                return Task.FromResult(StepExecutionResult.OkWithManualFollowUp(
                    string.Join("\n", messages.Append(
                        "Manual actions required: " +
                        string.Join("; ", report.ManualActionsRequired)))));

            return Task.FromResult(StepExecutionResult.Ok(string.Join("\n", messages)));
        }
        catch (Exception ex)
        {
            _log.Error($"{tag}: Unexpected exception.", ex, "Rollback");
            if (config.OracleRollback is null) config.OracleRollback = report;
            else config.OracleRollback.MergeFrom(report);
            return Task.FromResult(StepExecutionResult.Fail(
                $"JavaHome rollback failed: {ex.Message}", 1, ex));
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// OracleRollbackVerificationService
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Post-rollback verification utility.
/// Checks whether the Oracle environment is clean after a rollback pass.
///
/// Checks:
///   ✔ Target Oracle home is no longer registered in the Central Inventory
///   ✔ Target Oracle home directory does not exist on disk
///   ✔ No OUI inventory lock files remain in the inventory directory
///   ✔ No orphan OUI/java-OUI processes are still running
/// </summary>
public static class OracleRollbackVerificationService
{
    public static OracleRollbackVerificationResult Verify(
        IEnumerable<string>     homePaths,
        string?                 inventoryPath,
        IOracleInventoryService inventory,
        ILoggingService         log,
        string                  tag)
    {
        var findings          = new List<string>();
        var warnings          = new List<string>();
        var manualActions     = new List<string>();
        var allClean          = true;

        // ── Check 1: Home directories are gone ────────────────────────────────
        foreach (var homePath in homePaths)
        {
            if (Directory.Exists(homePath))
            {
                allClean = false;
                findings.Add($"✗ Oracle home directory still exists: '{homePath}'");
                log.Warning(
                    $"{tag}: Verification — home directory still exists: '{homePath}'",
                    "Rollback");
                manualActions.Add(
                    $"Manually delete Oracle home directory: '{homePath}'");
            }
            else
            {
                findings.Add($"✔ Oracle home directory removed: '{homePath}'");
            }
        }

        // ── Check 2: Central Inventory no longer registers these homes ─────────
        if (!string.IsNullOrWhiteSpace(inventoryPath))
        {
            try
            {
                var snapshot = inventory.ReadSnapshot(inventoryPath);

                foreach (var homePath in homePaths)
                {
                    var stillRegistered = snapshot?.OracleHomes
                        .Any(h => h.Path.TrimEnd('\\').Equals(
                            homePath.TrimEnd('\\'),
                            StringComparison.OrdinalIgnoreCase)) ?? false;

                    if (stillRegistered)
                    {
                        allClean = false;
                        findings.Add($"✗ Oracle home still registered in inventory: '{homePath}'");
                        log.Warning(
                            $"{tag}: Verification — '{homePath}' is still in Central Inventory.",
                            "Rollback");
                        manualActions.Add(
                            $"Run: $ORACLE_HOME/oui/bin/detachHome.sh -silent -jreLoc ... " +
                            $"to detach '{homePath}' from Central Inventory, or edit " +
                            $"'{Path.Combine(inventoryPath, "ContentsXML", "inventory.xml")}' manually.");
                    }
                    else
                    {
                        findings.Add($"✔ Oracle home not registered in inventory: '{homePath}'");
                    }
                }

                // ── Check 3: No inventory lock files ──────────────────────────
                var lockPattern = Path.Combine(inventoryPath, "ContentsXML", ".oracle.lock");
                var lockExists  = File.Exists(lockPattern) ||
                    Directory.GetFiles(inventoryPath, "*.lock", SearchOption.AllDirectories).Length > 0;

                if (lockExists)
                {
                    findings.Add($"⚠ OUI lock file(s) detected in inventory at '{inventoryPath}'");
                    warnings.Add($"OUI lock files remain in '{inventoryPath}' — may block future installs.");
                    log.Warning(
                        $"{tag}: Verification — OUI lock files remain in '{inventoryPath}'.",
                        "Rollback");
                }
                else
                {
                    findings.Add($"✔ No OUI lock files in inventory");
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not verify Central Inventory state: {ex.Message}");
                log.Warning(
                    $"{tag}: Verification — inventory check failed: {ex.Message}",
                    "Rollback");
            }
        }
        else
        {
            warnings.Add("Inventory path not configured — Central Inventory verification skipped.");
        }

        log.Info(
            $"{tag}: Verification complete — {(allClean ? "CLEAN" : "RESIDUALS DETECTED")}. " +
            $"{findings.Count} finding(s), {manualActions.Count} manual action(s).",
            "Rollback");

        return new OracleRollbackVerificationResult
        {
            IsClean              = allClean,
            Findings             = findings.AsReadOnly(),
            RemainingWarnings    = warnings.AsReadOnly(),
            ManualActionsRequired = manualActions.AsReadOnly()
        };
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// OracleRollbackCore — shared rollback primitive operations
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Shared helper methods used by all Oracle rollback executors.
/// Encapsulates the primitive destructive operations:
///   • Process detection and termination
///   • Windows service stop and delete
///   • Environment variable removal
///   • Registry key removal
///   • Central Inventory detach (via IOracleInventoryService)
///   • Directory and file deletion
///
/// All methods respect <paramref name="dryRun"/> — when true, they log the
/// planned action but skip the destructive operation.
/// </summary>
public sealed class OracleRollbackCore
{
    private readonly ILoggingService         _log;
    private readonly IOracleInventoryService _inventory;
    private readonly IOracleProcessManager   _processManager;

    public OracleRollbackCore(
        ILoggingService         log,
        IOracleInventoryService inventory,
        IOracleProcessManager   processManager)
    {
        _log            = log;
        _inventory      = inventory;
        _processManager = processManager;
    }

    // ── Process Management ────────────────────────────────────────────────────

    public async Task<ProcessStopSummary> StopOracleProcessesAsync(
        DeploymentConfiguration config,
        bool                    dryRun,
        string                  tag,
        CancellationToken       cancellationToken)
    {
        var summary  = new ProcessStopSummary();
        var detected = _processManager.DetectMiddlewareProcesses();

        if (detected.Count == 0)
        {
            _log.Info($"{tag}: No Oracle middleware processes detected.", "Rollback");
            summary.Messages.Add("No Oracle middleware processes detected.");
            return summary;
        }

        _log.Info(
            $"{tag}: Detected {detected.Count} Oracle middleware process(es): " +
            string.Join(", ", detected.Select(p => $"{p.ProcessName}({p.ProcessId})")),
            "Rollback");

        if (dryRun)
        {
            foreach (var p in detected)
                summary.Messages.Add(
                    $"[DRY-RUN] Would stop: {p.ProcessName} (PID {p.ProcessId})");
            return summary;
        }

        var timeout  = TimeSpan.FromSeconds(config.OracleLifecycle.ProcessShutdownTimeoutSeconds);
        var force    = config.OracleLifecycle.ForceKillProcessesOnRollback;
        var result   = await _processManager.StopProcessesAsync(
            detected, force, timeout, cancellationToken);

        foreach (var msg in result.Messages)
            summary.Messages.Add(msg);

        summary.StoppedDescriptions.AddRange(
            detected.Select(p => $"PID {p.ProcessId} {p.ProcessName}"));

        if (result.FailedCount > 0)
            _log.Warning(
                $"{tag}: {result.FailedCount} process(es) could not be stopped.",
                "Rollback");
        else
            _log.Info(
                $"{tag}: Stopped {result.StoppedCount} Oracle process(es).",
                "Rollback");

        return summary;
    }

    // ── Service Management ────────────────────────────────────────────────────

    public async Task<ServiceRemoveResult> StopAndRemoveServiceAsync(
        string            serviceName,
        bool              dryRun,
        string            tag,
        CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            _log.Info($"{tag}: [DRY-RUN] Would stop and delete service '{serviceName}'.", "Rollback");
            return new ServiceRemoveResult
            {
                Message = $"[DRY-RUN] Would stop and remove Windows service '{serviceName}'.",
                Removed = false
            };
        }

        // Stop
        var stopOutput = await RunScAsync($"stop \"{serviceName}\"", cancellationToken);
        if (!stopOutput.success)
        {
            _log.Warning(
                $"{tag}: sc stop '{serviceName}' returned: {stopOutput.output} — proceeding to delete.",
                "Rollback");
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        // Delete
        var deleteOutput = await RunScAsync($"delete \"{serviceName}\"", cancellationToken);
        if (!deleteOutput.success)
        {
            _log.Warning(
                $"{tag}: sc delete '{serviceName}' failed: {deleteOutput.output}",
                "Rollback");
            return new ServiceRemoveResult
            {
                Message = $"Failed to delete service '{serviceName}': {deleteOutput.output}",
                Removed = false,
                Warning = $"Service '{serviceName}' could not be deleted — remove manually via 'sc delete {serviceName}'."
            };
        }

        _log.Info($"{tag}: Service '{serviceName}' stopped and deleted.", "Rollback");
        return new ServiceRemoveResult
        {
            Message = $"Windows service '{serviceName}' stopped and removed.",
            Removed = true
        };
    }

    // ── Environment Variables ─────────────────────────────────────────────────

    public EnvVarRemoveResult RemoveEnvironmentVariable(
        string varName,
        bool   dryRun,
        string tag)
    {
        try
        {
            var current = Environment.GetEnvironmentVariable(
                varName, EnvironmentVariableTarget.Machine);

            if (current is null)
            {
                return new EnvVarRemoveResult
                {
                    Message = $"Machine env var '{varName}' not set — already absent.",
                    Removed = false
                };
            }

            if (dryRun)
            {
                _log.Info($"{tag}: [DRY-RUN] Would remove machine env var {varName}='{current}'.", "Rollback");
                return new EnvVarRemoveResult
                {
                    Message = $"[DRY-RUN] Would remove machine env var '{varName}' (current: '{current}').",
                    Removed = false
                };
            }

            Environment.SetEnvironmentVariable(varName, null, EnvironmentVariableTarget.Machine);
            _log.Info($"{tag}: Removed machine env var '{varName}'.", "Rollback");
            return new EnvVarRemoveResult
            {
                Message = $"Machine env var '{varName}' removed.",
                Removed = true
            };
        }
        catch (UnauthorizedAccessException uae)
        {
            _log.Error($"{tag}: Cannot remove env var '{varName}' — access denied.", uae, "Rollback");
            return new EnvVarRemoveResult
            {
                Message = $"Cannot remove '{varName}' — Administrator privileges required.",
                Removed = false,
                Warning = $"Remove machine env var '{varName}' manually via System Properties → Environment Variables."
            };
        }
    }

    // ── Registry ──────────────────────────────────────────────────────────────

    public RegKeyRemoveResult RemoveRegistryKey(string fullKeyPath, bool dryRun, string tag)
    {
        // fullKeyPath example: "SOFTWARE\ORACLE\KEY_WEDM_OracleMW"
        try
        {
            // Split into parent key and subkey name
            var lastSep  = fullKeyPath.LastIndexOf('\\');
            var parentPath = lastSep > 0 ? fullKeyPath[..lastSep] : string.Empty;
            var subKeyName = lastSep > 0 ? fullKeyPath[(lastSep + 1)..] : fullKeyPath;

            if (string.IsNullOrEmpty(parentPath))
            {
                return new RegKeyRemoveResult
                {
                    Message = $"Cannot remove root-level registry key '{fullKeyPath}' — path must include a parent."
                };
            }

            using var parentKey = Registry.LocalMachine.OpenSubKey(parentPath, writable: !dryRun);
            if (parentKey is null)
            {
                return new RegKeyRemoveResult
                {
                    Message = $"Registry parent key HKLM\\{parentPath} not found — '{subKeyName}' already absent."
                };
            }

            var subNames = parentKey.GetSubKeyNames();
            if (!subNames.Contains(subKeyName, StringComparer.OrdinalIgnoreCase))
            {
                return new RegKeyRemoveResult
                {
                    Message = $"Registry key HKLM\\{fullKeyPath} not found — already absent."
                };
            }

            if (dryRun)
            {
                _log.Info($"{tag}: [DRY-RUN] Would delete registry key HKLM\\{fullKeyPath}.", "Rollback");
                return new RegKeyRemoveResult
                {
                    Message = $"[DRY-RUN] Would delete registry key HKLM\\{fullKeyPath}."
                };
            }

            parentKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
            _log.Info($"{tag}: Deleted registry key HKLM\\{fullKeyPath}.", "Rollback");
            return new RegKeyRemoveResult
            {
                Message = $"Registry key HKLM\\{fullKeyPath} deleted."
            };
        }
        catch (UnauthorizedAccessException uae)
        {
            _log.Error($"{tag}: Cannot remove registry key '{fullKeyPath}' — access denied.", uae, "Rollback");
            return new RegKeyRemoveResult
            {
                Message = $"Cannot remove HKLM\\{fullKeyPath} — Administrator privileges required."
            };
        }
        catch (Exception ex)
        {
            _log.Warning($"{tag}: Failed to remove registry key '{fullKeyPath}': {ex.Message}", "Rollback");
            return new RegKeyRemoveResult
            {
                Message = $"Failed to remove HKLM\\{fullKeyPath}: {ex.Message}"
            };
        }
    }

    // ── Oracle Inventory Detach ───────────────────────────────────────────────

    public InventoryDetachSummary DetachFromInventory(
        string  homePath,
        string? inventoryPath,
        bool    dryRun,
        string  tag)
    {
        if (string.IsNullOrWhiteSpace(inventoryPath))
        {
            return new InventoryDetachSummary
            {
                Message  = "Oracle inventory path not configured — skipping inventory detach.",
                Detached = false,
                HomeName = Path.GetFileName(homePath)
            };
        }

        _log.Info(
            $"{tag}: Detaching '{homePath}' from Central Inventory at '{inventoryPath}'.",
            "Rollback");

        if (dryRun)
        {
            return new InventoryDetachSummary
            {
                Message  = $"[DRY-RUN] Would detach '{homePath}' from Central Inventory.",
                Detached = false,
                HomeName = Path.GetFileName(homePath)
            };
        }

        var result = _inventory.RemoveHomeEntry(homePath, inventoryPath);

        if (!result.HomeWasRegistered)
        {
            return new InventoryDetachSummary
            {
                Message  = $"'{homePath}' was not registered in Central Inventory — no detach required.",
                Detached = false,
                HomeName = Path.GetFileName(homePath)
            };
        }

        if (result.Success)
        {
            _log.Info(
                $"{tag}: Inventory detach ✔ — '{homePath}' removed from Central Inventory. " +
                $"Backup: '{result.BackupPath}'.",
                "Rollback");
            return new InventoryDetachSummary
            {
                Message  = $"Oracle inventory: detached '{homePath}'. Backup: '{result.BackupPath}'.",
                Detached = true,
                HomeName = Path.GetFileName(homePath)
            };
        }

        _log.Warning(
            $"{tag}: Inventory detach failed for '{homePath}': {result.Error}",
            "Rollback");
        return new InventoryDetachSummary
        {
            Message  = $"WARNING: Inventory detach failed for '{homePath}': {result.Error}",
            Detached = false,
            HomeName = Path.GetFileName(homePath)
        };
    }

    // ── Filesystem ────────────────────────────────────────────────────────────

    public FsDeleteResult DeleteDirectory(string path, bool dryRun, string tag)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FsDeleteResult { Message = "Directory path empty — skipped.", Deleted = false };

        if (!Directory.Exists(path))
            return new FsDeleteResult
            {
                Message = $"Directory '{path}' not found — already absent.",
                Deleted = false
            };

        if (dryRun)
        {
            _log.Info($"{tag}: [DRY-RUN] Would delete directory '{path}'.", "Rollback");
            return new FsDeleteResult
            {
                Message = $"[DRY-RUN] Would delete directory '{path}'.",
                Deleted = false
            };
        }

        try
        {
            Directory.Delete(path, recursive: true);
            _log.Info($"{tag}: Deleted directory '{path}'.", "Rollback");
            return new FsDeleteResult { Message = $"Directory '{path}' deleted.", Deleted = true };
        }
        catch (IOException ioEx)
        {
            _log.Warning($"{tag}: Directory '{path}' is locked: {ioEx.Message}", "Rollback");
            return new FsDeleteResult
            {
                Message = $"Cannot delete '{path}' — files in use: {ioEx.Message}",
                Deleted = false,
                Warning = $"Directory '{path}' could not be deleted (files in use). " +
                          "Close all Oracle/Java processes and delete manually."
            };
        }
        catch (Exception ex)
        {
            _log.Warning($"{tag}: Cannot delete '{path}': {ex.Message}", "Rollback");
            return new FsDeleteResult
            {
                Message  = $"Cannot delete '{path}': {ex.Message}",
                Deleted  = false,
                Warning  = $"Delete '{path}' manually."
            };
        }
    }

    public FsDeleteResult DeleteFile(string path, bool dryRun, string tag)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FsDeleteResult { Message = "File path empty — skipped.", Deleted = false };

        if (!File.Exists(path))
            return new FsDeleteResult
            {
                Message = $"File '{path}' not found — already absent.",
                Deleted = false
            };

        if (dryRun)
        {
            _log.Info($"{tag}: [DRY-RUN] Would delete file '{path}'.", "Rollback");
            return new FsDeleteResult
            {
                Message = $"[DRY-RUN] Would delete file '{path}'.",
                Deleted = false
            };
        }

        try
        {
            File.Delete(path);
            _log.Info($"{tag}: Deleted file '{path}'.", "Rollback");
            return new FsDeleteResult { Message = $"File '{path}' deleted.", Deleted = true };
        }
        catch (Exception ex)
        {
            _log.Warning($"{tag}: Cannot delete file '{path}': {ex.Message}", "Rollback");
            return new FsDeleteResult
            {
                Message = $"Cannot delete file '{path}': {ex.Message}",
                Deleted = false
            };
        }
    }

    // ── Inventory Lock Check ──────────────────────────────────────────────────

    public static bool HasInventoryLocks(string? inventoryPath)
    {
        if (string.IsNullOrWhiteSpace(inventoryPath) || !Directory.Exists(inventoryPath))
            return false;

        try
        {
            return Directory.GetFiles(inventoryPath, "*.lock", SearchOption.AllDirectories).Length > 0
                || File.Exists(Path.Combine(inventoryPath, "ContentsXML", ".oracle.lock"));
        }
        catch
        {
            return false;
        }
    }

    // ── Service Name Inference (config fallback) ──────────────────────────────

    public static List<string> InferMiddlewareServiceNames(DeploymentConfiguration config)
    {
        var names = new List<string>();
        names.Add($"WLS_{config.Domain.AdminServerName}");
        names.Add(config.Domain.NodeManager.ServiceName);
        foreach (var ms in config.Domain.ManagedServers.Where(m => m.RegisterService))
            names.Add($"WLS_{ms.Name}");
        return names;
    }

    public static List<string> InferFormsServicesNames(DeploymentConfiguration config)
    {
        // Forms typically registers an OHS component service
        return [$"OracleOHSComponent_{config.Domain.DomainName}1"];
    }

    public static List<string> InferOhsServiceNames(DeploymentConfiguration config)
    {
        return [$"OracleOHSComponent_{config.Domain.DomainName}1"];
    }

    public static List<string> InferOracleEnvVarNames()
        => ["ORACLE_HOME", "ORACLE_BASE"];

    // ── sc.exe helper ─────────────────────────────────────────────────────────

    private static async Task<(bool success, string output)> RunScAsync(
        string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "sc.exe",
                Arguments              = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start sc.exe.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var combined = ((await stdoutTask) + (await stderrTask)).Trim();
            // 0 = success; 1060 = service does not exist (acceptable for stop)
            return (process.ExitCode == 0 || process.ExitCode == 1060, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Small result types used by OracleRollbackCore
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ProcessStopSummary
{
    public List<string> Messages            { get; } = [];
    public List<string> StoppedDescriptions { get; } = [];
}

public sealed class ServiceRemoveResult
{
    public string  Message { get; init; } = string.Empty;
    public bool    Removed { get; init; }
    public string? Warning { get; init; }
}

public sealed class EnvVarRemoveResult
{
    public string  Message { get; init; } = string.Empty;
    public bool    Removed { get; init; }
    public string? Warning { get; init; }
}

public sealed class RegKeyRemoveResult
{
    public string Message { get; init; } = string.Empty;
}

public sealed class InventoryDetachSummary
{
    public string Message  { get; init; } = string.Empty;
    public bool   Detached { get; init; }
    public string HomeName { get; init; } = string.Empty;
}

public sealed class FsDeleteResult
{
    public string  Message { get; init; } = string.Empty;
    public bool    Deleted { get; init; }
    public string? Warning { get; init; }
}
