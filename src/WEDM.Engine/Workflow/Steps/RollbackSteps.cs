using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Workflow.Steps;

// ── Remove-OracleFolders ──────────────────────────────────────────────────────

/// <summary>
/// Rollback for CreateOracleFolders.
///
/// Sequence:
///   1. Remove stale Oracle Central Inventory registration for the target home (if any)
///   2. Remove the Oracle inventory directory (if WEDM-created)
///   3. Remove the Oracle root directory only if it contains no live installations
///
/// Uses <see cref="IOracleInventoryService"/> to surgically remove any stale inventory
/// registration before filesystem deletion, preventing INST-07319 on retry.
/// </summary>
public sealed class RemoveOracleFoldersStep : IStepExecutor
{
    private readonly ILoggingService         _log;
    private readonly IOracleInventoryService _inventory;

    public RemoveOracleFoldersStep(ILoggingService log, IOracleInventoryService inventory)
    {
        _log       = log;
        _inventory = inventory;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var removed = new List<string>();
            var skipped = new List<string>();
            var messages = new List<string>();

            // ── Step 1: Oracle inventory registration cleanup ──────────────
            // Must happen BEFORE filesystem deletion so the XML can still be parsed.
            var inventoryPath = config.Paths.OracleInventory;
            var mwHome        = config.Paths.MiddlewareHome;

            if (!string.IsNullOrWhiteSpace(mwHome) && !string.IsNullOrWhiteSpace(inventoryPath))
            {
                _log.Info(
                    $"Remove-OracleFolders: Checking Central Inventory for '{mwHome}' registration.",
                    "Rollback");

                var removalResult = _inventory.RemoveHomeEntry(mwHome, inventoryPath);

                if (removalResult.HomeWasRegistered)
                {
                    if (removalResult.Success)
                    {
                        messages.Add(
                            $"Oracle inventory: removed registration for '{mwHome}'. " +
                            $"Backup: '{removalResult.BackupPath}'. " +
                            $"Removed entries: {string.Join(", ", removalResult.RemovedEntries)}");
                        _log.Info(
                            $"Remove-OracleFolders: Inventory registration removed ✔ " +
                            $"(before: {removalResult.SnapshotBefore?.OracleHomes.Count} homes, " +
                            $"after: {removalResult.SnapshotAfter?.OracleHomes.Count} homes)",
                            "Rollback");
                    }
                    else
                    {
                        messages.Add($"WARNING: Inventory removal failed — {removalResult.Error}. Filesystem cleanup will still proceed.");
                        _log.Warning(
                            $"Remove-OracleFolders: Inventory removal failed: {removalResult.Error}. " +
                            "Continuing with filesystem cleanup.",
                            "Rollback");
                    }
                }
                else
                {
                    _log.Info(
                        $"Remove-OracleFolders: '{mwHome}' was not registered in Central Inventory — no inventory cleanup needed.",
                        "Rollback");
                    messages.Add("Oracle inventory: target home was not registered — no inventory cleanup required.");
                }
            }

            // ── Step 2: Oracle inventory directory ────────────────────────
            if (Directory.Exists(inventoryPath))
            {
                TryRemoveDirectory(inventoryPath, removed, skipped, "Oracle inventory");
            }

            // ── Step 3: Oracle root directory ─────────────────────────────
            var oracleRoot = config.Paths.OracleRoot;
            if (Directory.Exists(oracleRoot))
            {
                // Check for a live WLS installation marker inside root
                var wlserver = Path.Combine(mwHome, "wlserver");
                if (Directory.Exists(wlserver))
                {
                    _log.Warning(
                        $"Remove-OracleFolders: wlserver directory detected at '{wlserver}'. " +
                        "Middleware home appears to contain a live installation — skipping Oracle root removal.",
                        "Rollback");
                    skipped.Add(oracleRoot);
                }
                else
                {
                    // Only attempt root removal if it is empty (or only has temp/logs/reports/snapshots)
                    var safeSubDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "temp", "wedm", "orainventory", "orainv", "logs", "reports", "snapshots"
                    };

                    var topDirs = Directory.GetDirectories(oracleRoot)
                        .Select(d => Path.GetFileName(d) ?? "")
                        .Where(n => !safeSubDirs.Contains(n))
                        .ToList();

                    if (topDirs.Count == 0)
                    {
                        TryRemoveDirectory(oracleRoot, removed, skipped, "Oracle root");
                    }
                    else
                    {
                        _log.Warning(
                            $"Remove-OracleFolders: Oracle root '{oracleRoot}' contains unexpected directories " +
                            $"[{string.Join(", ", topDirs)}] — skipping to avoid data loss.",
                            "Rollback");
                        skipped.Add(oracleRoot);
                    }
                }
            }

            if (removed.Count > 0)
                messages.Add($"Removed directories: {string.Join(", ", removed)}");
            if (skipped.Count > 0)
                messages.Add($"Skipped directories: {string.Join(", ", skipped)}");
            if (messages.Count == 0)
                messages.Add("No Oracle directories found — already clean.");

            return Task.FromResult(StepExecutionResult.Ok(string.Join(". ", messages)));
        }
        catch (Exception ex)
        {
            _log.Error("Remove-OracleFolders rollback failed.", ex, "Rollback");
            return Task.FromResult(StepExecutionResult.Fail(
                $"Failed to remove Oracle folders: {ex.Message}", 1, ex));
        }
    }

    private void TryRemoveDirectory(string path, List<string> removed, List<string> skipped, string label)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            _log.Info($"Remove-OracleFolders: Removed {label} at '{path}'.", "Rollback");
            removed.Add(path);
        }
        catch (Exception ex)
        {
            _log.Warning($"Remove-OracleFolders: Could not remove {label} '{path}': {ex.Message}", "Rollback");
            skipped.Add(path);
        }
    }
}

// ── Remove-JDK ────────────────────────────────────────────────────────────────

/// <summary>
/// Rollback for InstallJDK.
/// Removes the JDK directory only when it was placed in a WEDM-managed location
/// (path contains "wedm" or "wlstjdk", case-insensitive).
/// System JDKs (e.g. C:\Program Files\Java) are logged and left untouched.
/// </summary>
public sealed class RemoveJdkStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public RemoveJdkStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var javaHome = config.Java.JavaHome;

            if (string.IsNullOrWhiteSpace(javaHome))
            {
                _log.Info("Remove-JDK: No JavaHome configured — nothing to remove.", "Rollback");
                return Task.FromResult(StepExecutionResult.Ok("JavaHome not configured — already clean."));
            }

            if (!Directory.Exists(javaHome))
            {
                _log.Info($"Remove-JDK: JDK path '{javaHome}' does not exist — already clean.", "Rollback");
                return Task.FromResult(StepExecutionResult.Ok($"JDK path '{javaHome}' not found — already clean."));
            }

            // Safety check: only auto-remove WEDM-managed JDK installs
            var pathLower = javaHome.ToLowerInvariant();
            var isWedmManaged = pathLower.Contains("wedm") || pathLower.Contains("wlstjdk");

            if (!isWedmManaged)
            {
                _log.Warning(
                    $"Remove-JDK: JDK at '{javaHome}' does not appear to be WEDM-managed " +
                    "(path does not contain 'wedm' or 'wlstjdk'). Skipping automatic removal to prevent " +
                    "damage to a system-installed JDK. Remove manually if required.",
                    "Rollback");
                return Task.FromResult(StepExecutionResult.OkWithManualFollowUp(
                    $"Skipped: JDK at '{javaHome}' appears to be system-installed. " +
                    "The path does not match WEDM-managed patterns (wedm/wlstjdk); remove manually if required."));
            }

            Directory.Delete(javaHome, recursive: true);
            _log.Info($"Remove-JDK: Removed JDK directory '{javaHome}'.", "Rollback");
            return Task.FromResult(StepExecutionResult.Ok($"JDK directory '{javaHome}' removed."));
        }
        catch (Exception ex)
        {
            _log.Error("Remove-JDK rollback failed.", ex, "Rollback");
            return Task.FromResult(StepExecutionResult.Fail(
                $"Failed to remove JDK: {ex.Message}", 1, ex));
        }
    }
}

// ── Remove-JavaEnvVars ────────────────────────────────────────────────────────

/// <summary>
/// Rollback for ConfigureJavaHome.
/// Removes JAVA_HOME from Machine-level environment variables when it matches
/// the configured JavaHome path.  Strips the JavaHome bin directory from
/// the Machine-level PATH.
/// Requires Administrator privileges — returns Fail with remediation hint if not.
/// </summary>
public sealed class RemoveJavaEnvVarsStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public RemoveJavaEnvVarsStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var javaHome = config.Java.JavaHome;
            var actions  = new List<string>();

            // ── JAVA_HOME ──────────────────────────────────────────────────────
            try
            {
                var currentJavaHome = Environment.GetEnvironmentVariable(
                    "JAVA_HOME", EnvironmentVariableTarget.Machine);

                if (currentJavaHome is not null &&
                    string.Equals(currentJavaHome.TrimEnd('\\'),
                                  javaHome.TrimEnd('\\'),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable(
                        "JAVA_HOME", null, EnvironmentVariableTarget.Machine);
                    _log.Info("Remove-JavaEnvVars: JAVA_HOME removed from machine environment.", "Rollback");
                    actions.Add("JAVA_HOME removed");
                }
                else
                {
                    _log.Info(
                        $"Remove-JavaEnvVars: JAVA_HOME ('{currentJavaHome}') does not match " +
                        $"configured JavaHome ('{javaHome}') — skipped.",
                        "Rollback");
                    actions.Add("JAVA_HOME skipped (value mismatch or already removed)");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Error("Remove-JavaEnvVars: Insufficient privileges to modify JAVA_HOME.", ex, "Rollback");
                return Task.FromResult(StepExecutionResult.Fail(
                    "Cannot remove JAVA_HOME — Administrator privileges required. " +
                    "Run WEDM as Administrator and retry, or remove manually via System Properties → Environment Variables.",
                    1, ex, retryRecommended: false));
            }

            // ── PATH ───────────────────────────────────────────────────────────
            try
            {
                var javaBin = Path.Combine(javaHome, "bin");
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
                    Environment.SetEnvironmentVariable(
                        "PATH",
                        string.Join(";", filtered),
                        EnvironmentVariableTarget.Machine);
                    _log.Info($"Remove-JavaEnvVars: Removed '{javaBin}' from machine PATH.", "Rollback");
                    actions.Add($"'{javaBin}' removed from PATH");
                }
                else
                {
                    _log.Info(
                        $"Remove-JavaEnvVars: '{javaBin}' not found in machine PATH — skipped.",
                        "Rollback");
                    actions.Add("PATH entry already absent");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Error("Remove-JavaEnvVars: Insufficient privileges to modify system PATH.", ex, "Rollback");
                return Task.FromResult(StepExecutionResult.Fail(
                    "Cannot modify system PATH — Administrator privileges required. " +
                    "Run WEDM as Administrator and retry, or remove manually via System Properties → Environment Variables.",
                    1, ex, retryRecommended: false));
            }

            return Task.FromResult(StepExecutionResult.Ok(string.Join("; ", actions)));
        }
        catch (Exception ex)
        {
            _log.Error("Remove-JavaEnvVars rollback failed.", ex, "Rollback");
            return Task.FromResult(StepExecutionResult.Fail(
                $"Failed to remove Java environment variables: {ex.Message}", 1, ex));
        }
    }
}

// ── Remove-MiddlewareHome ─────────────────────────────────────────────────────

/// <summary>
/// Rollback for InstallInfrastructure / InstallWebLogic.
///
/// Sequence:
///   1. Remove stale Oracle Central Inventory registration for the MW home (if any)
///   2. Check for active Windows services referencing the MW home (abort if found)
///   3. Warn if domain home is nested under MW home
///   4. Delete the middleware home directory tree
///
/// HIGH RISK: checks for active Windows services referencing the MW home before
/// attempting deletion.  Returns Fail with remediation instructions if services
/// are still running.
///
/// Uses <see cref="IOracleInventoryService"/> to surgically remove any stale inventory
/// registration before filesystem deletion, preventing INST-07319 on retry.
/// </summary>
public sealed class RemoveMiddlewareHomeStep : IStepExecutor
{
    private readonly ILoggingService         _log;
    private readonly IOracleInventoryService _inventory;

    public RemoveMiddlewareHomeStep(ILoggingService log, IOracleInventoryService inventory)
    {
        _log       = log;
        _inventory = inventory;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var mwHome        = config.Paths.MiddlewareHome;
        var inventoryPath = config.Paths.OracleInventory;
        var messages      = new List<string>();

        try
        {
            // ── Step 1: Oracle inventory registration cleanup ──────────────────
            // Must happen BEFORE filesystem deletion so inventory.xml is still readable.
            if (!string.IsNullOrWhiteSpace(mwHome) && !string.IsNullOrWhiteSpace(inventoryPath))
            {
                _log.Info(
                    $"Remove-MiddlewareHome: Checking Central Inventory for '{mwHome}' registration.",
                    "Rollback");

                var removalResult = _inventory.RemoveHomeEntry(mwHome, inventoryPath);

                if (removalResult.HomeWasRegistered)
                {
                    if (removalResult.Success)
                    {
                        messages.Add(
                            $"Oracle inventory: removed registration for '{mwHome}'. " +
                            $"Backup: '{removalResult.BackupPath}'. " +
                            $"Removed entries: {string.Join(", ", removalResult.RemovedEntries)}");
                        _log.Info(
                            $"Remove-MiddlewareHome: Inventory registration removed ✔ " +
                            $"(before: {removalResult.SnapshotBefore?.OracleHomes.Count} homes, " +
                            $"after: {removalResult.SnapshotAfter?.OracleHomes.Count} homes)",
                            "Rollback");
                    }
                    else
                    {
                        messages.Add($"WARNING: Inventory removal failed — {removalResult.Error}. Filesystem cleanup will still proceed.");
                        _log.Warning(
                            $"Remove-MiddlewareHome: Inventory removal failed: {removalResult.Error}. " +
                            "Continuing with filesystem cleanup.",
                            "Rollback");
                    }
                }
                else
                {
                    _log.Info(
                        $"Remove-MiddlewareHome: '{mwHome}' was not registered in Central Inventory — no inventory cleanup needed.",
                        "Rollback");
                    messages.Add("Oracle inventory: target home was not registered — no inventory cleanup required.");
                }
            }

            // ── Step 2: Early-exit if MW home is already gone ─────────────────
            if (!Directory.Exists(mwHome))
            {
                _log.Info($"Remove-MiddlewareHome: '{mwHome}' does not exist — already clean.", "Rollback");
                messages.Add($"Middleware home '{mwHome}' not found — already clean.");
                return Task.FromResult(StepExecutionResult.Ok(string.Join(". ", messages)));
            }

            // ── Step 3: Active service check ───────────────────────────────────
            var activeServices = FindServicesReferencingPath(mwHome);
            if (activeServices.Count > 0)
            {
                var svcList = string.Join(", ", activeServices);
                _log.Warning(
                    $"Remove-MiddlewareHome: Cannot remove '{mwHome}' — active Windows services reference it: {svcList}",
                    "Rollback");
                return Task.FromResult(StepExecutionResult.Fail(
                    $"Active Windows services reference this middleware home: [{svcList}]. " +
                    "Stop and remove these services before retrying the rollback.",
                    1, retryRecommended: true));
            }

            // ── Step 4: Domain-under-MW warning ────────────────────────────────
            var domainHome = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);
            if (IsSubPath(mwHome, domainHome))
            {
                _log.Warning(
                    $"Remove-MiddlewareHome: Domain home '{domainHome}' is under middleware home '{mwHome}'. " +
                    "The domain will also be removed.",
                    "Rollback");
            }

            // ── Step 5: Deletion ───────────────────────────────────────────────
            try
            {
                Directory.Delete(mwHome, recursive: true);
                _log.Info($"Remove-MiddlewareHome: Removed middleware home '{mwHome}'.", "Rollback");
                messages.Add($"Middleware home '{mwHome}' removed.");
                return Task.FromResult(StepExecutionResult.Ok(string.Join(". ", messages)));
            }
            catch (IOException ioEx)
            {
                _log.Error($"Remove-MiddlewareHome: Files in use under '{mwHome}'.", ioEx, "Rollback");
                return Task.FromResult(StepExecutionResult.Fail(
                    $"Files in use under '{mwHome}' — close all Oracle/Java processes and retry. " +
                    $"Detail: {ioEx.Message}",
                    1, ioEx, retryRecommended: true));
            }
        }
        catch (Exception ex)
        {
            _log.Error("Remove-MiddlewareHome rollback failed.", ex, "Rollback");
            return Task.FromResult(StepExecutionResult.Fail(
                $"Failed to remove middleware home: {ex.Message}", 1, ex));
        }
    }

    private static List<string> FindServicesReferencingPath(string mwHome)
    {
        var result = new List<string>();
        try
        {
            // Query the SCM image path for each service via registry
            using var servicesKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services");
            if (servicesKey is null) return result;

            foreach (var svcName in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var svcKey = servicesKey.OpenSubKey(svcName);
                    var imagePath = svcKey?.GetValue("ImagePath")?.ToString();
                    if (imagePath is not null &&
                        imagePath.Contains(mwHome, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(svcName);
                    }
                }
                catch
                {
                    // Skip unreadable service keys
                }
            }
        }
        catch
        {
            // If registry access fails, be conservative — return empty (proceed with deletion)
        }
        return result;
    }

    private static bool IsSubPath(string parent, string child)
    {
        var parentFull = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
        var childFull  = Path.GetFullPath(child).TrimEnd('\\')  + "\\";
        return childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
    }
}

// ── Remove-Domain ─────────────────────────────────────────────────────────────

/// <summary>
/// Rollback for CreateDomain.
/// Removes the entire domain home directory after first invalidating the
/// config.xml sentinel to prevent accidental domain boot during cleanup.
/// </summary>
public sealed class RemoveDomainStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public RemoveDomainStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var domainHome = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);

        try
        {
            if (!Directory.Exists(domainHome))
            {
                _log.Info($"Remove-Domain: Domain home '{domainHome}' does not exist — already clean.", "Rollback");
                return Task.FromResult(StepExecutionResult.Ok(
                    $"Domain home '{domainHome}' not found — already clean."));
            }

            // Invalidate config.xml first to prevent an accidental boot during cleanup
            var cfgXml = Path.Combine(domainHome, "config", "config.xml");
            if (File.Exists(cfgXml))
            {
                try
                {
                    File.Delete(cfgXml);
                    _log.Info($"Remove-Domain: Removed config.xml sentinel at '{cfgXml}'.", "Rollback");
                }
                catch (Exception ex)
                {
                    _log.Warning($"Remove-Domain: Could not remove config.xml: {ex.Message}", "Rollback");
                }
            }

            // Remove the entire domain directory
            try
            {
                Directory.Delete(domainHome, recursive: true);
                _log.Info($"Remove-Domain: Domain home '{domainHome}' removed.", "Rollback");
                return Task.FromResult(StepExecutionResult.Ok($"Domain home '{domainHome}' removed."));
            }
            catch (IOException ioEx)
            {
                _log.Error($"Remove-Domain: Files in use under '{domainHome}'.", ioEx, "Rollback");
                return Task.FromResult(StepExecutionResult.Fail(
                    $"Files in use under '{domainHome}' — ensure no WebLogic server processes are running and retry. " +
                    $"Detail: {ioEx.Message}",
                    1, ioEx, retryRecommended: true));
            }
        }
        catch (Exception ex)
        {
            _log.Error("Remove-Domain rollback failed.", ex, "Rollback");
            return Task.FromResult(StepExecutionResult.Fail(
                $"Failed to remove domain home: {ex.Message}", 1, ex));
        }
    }
}

// ── Remove-{X}Service (generic) ───────────────────────────────────────────────

/// <summary>
/// Generic rollback executor for all Windows-service removal actions:
///   Remove-AdminService, Remove-NodeMgrService, Remove-{ManagedServerName}Service.
/// Extracts the service name from step.RollbackAction ("Remove-XService" → "X"),
/// stops the service if running, then deletes it via sc.exe.
/// If the service is not found, returns Ok (idempotent).
/// </summary>
public sealed class RemoveWindowsServiceStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public RemoveWindowsServiceStep(ILoggingService log) => _log = log;

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        // Derive the Windows service name from the rollback action
        // Pattern: "Remove-{ServiceName}Service"  → display-name fragment = ServiceName
        // The actual registered service name is resolved by probing ServiceController.
        var rollbackAction = step.RollbackAction ?? step.Name;
        var serviceFragment = ExtractServiceFragment(rollbackAction);

        if (string.IsNullOrEmpty(serviceFragment))
        {
            _log.Warning(
                $"RemoveWindowsServiceStep: Cannot derive service name from rollback action '{rollbackAction}'.",
                "Rollback");
            return StepExecutionResult.Fail(
                $"Cannot determine Windows service name from rollback action '{rollbackAction}'.");
        }

        _log.Info($"Remove-Service: Looking for Windows service matching '{serviceFragment}'.", "Rollback");

        // Find the service by matching the registered service name or display name
        string? serviceName = ResolveServiceName(serviceFragment);

        if (serviceName is null)
        {
            _log.Info(
                $"Remove-Service: No service matching '{serviceFragment}' found — already clean.",
                "Rollback");
            return StepExecutionResult.Ok(
                $"Service matching '{serviceFragment}' not registered — already clean.");
        }

        _log.Info($"Remove-Service: Found service '{serviceName}'. Stopping and removing.", "Rollback");

        // Stop the service
        var stopResult = await RunScAsync($"stop \"{serviceName}\"", cancellationToken);
        if (!stopResult.success)
        {
            // Non-fatal — service may already be stopped
            _log.Warning(
                $"Remove-Service: sc stop '{serviceName}' returned: {stopResult.output}. Proceeding to delete.",
                "Rollback");
        }
        else
        {
            _log.Info($"Remove-Service: Service '{serviceName}' stopped.", "Rollback");
            // Brief wait for the SCM to process the stop
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        // Delete the service
        var deleteResult = await RunScAsync($"delete \"{serviceName}\"", cancellationToken);
        if (!deleteResult.success)
        {
            _log.Error(
                $"Remove-Service: sc delete '{serviceName}' failed. Output: {deleteResult.output}",
                null, "Rollback");
            return StepExecutionResult.Fail(
                $"Failed to delete Windows service '{serviceName}': {deleteResult.output}",
                1, retryRecommended: true);
        }

        _log.Info($"Remove-Service: Service '{serviceName}' deleted from SCM.", "Rollback");
        return StepExecutionResult.Ok($"Windows service '{serviceName}' stopped and removed.");
    }

    /// <summary>
    /// Extracts the service name fragment from a rollback action string.
    /// "Remove-AdminService"      → "Admin"
    /// "Remove-NodeMgrService"    → "NodeMgr"
    /// "Remove-WLS_FORMSService"  → "WLS_FORMS"
    /// </summary>
    private static string ExtractServiceFragment(string rollbackAction)
    {
        const string prefix = "Remove-";
        const string suffix = "Service";

        if (!rollbackAction.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var middle = rollbackAction[prefix.Length..];

        if (middle.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            middle = middle[..^suffix.Length];

        return middle;
    }

    /// <summary>
    /// Locates the registered Windows service name by searching:
    ///   1. Exact match on ServiceName
    ///   2. Partial match on ServiceName (contains)
    ///   3. Partial match on DisplayName (contains)
    /// Returns null if no match found.
    /// </summary>
    private static string? ResolveServiceName(string fragment)
    {
        try
        {
            var all = ServiceController.GetServices();

            // Exact service name match first
            var exact = all.FirstOrDefault(s =>
                s.ServiceName.Equals(fragment, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.ServiceName;

            // Partial service name match
            var partial = all.FirstOrDefault(s =>
                s.ServiceName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            if (partial is not null) return partial.ServiceName;

            // Partial display name match
            var display = all.FirstOrDefault(s =>
                s.DisplayName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            if (display is not null) return display.ServiceName;
        }
        catch
        {
            // If ServiceController is unavailable, fall through — return null
        }
        return null;
    }

    private static async Task<(bool success, string output)> RunScAsync(
        string arguments,
        CancellationToken cancellationToken)
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
                ?? throw new InvalidOperationException("Failed to start sc.exe process.");

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask  = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            var stdout = await outputTask;
            var stderr = await errorTask;
            var combined = (stdout + stderr).Trim();

            // sc.exe exit 0 = success; 1060 = service does not exist (also acceptable for stop)
            var success = process.ExitCode == 0 || process.ExitCode == 1060;
            return (success, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

// ── Remove-OracleRegistryKeys ─────────────────────────────────────────────────

/// <summary>
/// Rollback for ConfigureRegistry.
/// Removes the HKLM\SOFTWARE\ORACLE\KEY_WEDM_OracleMW registry key created
/// by ConfigureRegistryStep.  Requires Administrator privileges.
/// </summary>
public sealed class RemoveOracleRegistryKeysStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public RemoveOracleRegistryKeysStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        const string oracleBaseKey = @"SOFTWARE\ORACLE";
        const string wedmSubKey    = "KEY_WEDM_OracleMW";
        var          fullKeyPath   = $@"{oracleBaseKey}\{wedmSubKey}";

        try
        {
            using var oracleKey = Registry.LocalMachine.OpenSubKey(oracleBaseKey, writable: true);

            if (oracleKey is null)
            {
                _log.Info(
                    $"Remove-OracleRegistryKeys: HKLM\\{oracleBaseKey} does not exist — already clean.",
                    "Rollback");
                return Task.FromResult(StepExecutionResult.Ok(
                    $"HKLM\\{oracleBaseKey} not found — already clean."));
            }

            var subKeyNames = oracleKey.GetSubKeyNames();
            if (!subKeyNames.Contains(wedmSubKey, StringComparer.OrdinalIgnoreCase))
            {
                _log.Info(
                    $"Remove-OracleRegistryKeys: Key HKLM\\{fullKeyPath} does not exist — already clean.",
                    "Rollback");
                return Task.FromResult(StepExecutionResult.Ok(
                    $"Registry key HKLM\\{fullKeyPath} not found — already clean."));
            }

            oracleKey.DeleteSubKeyTree(wedmSubKey, throwOnMissingSubKey: false);
            _log.Info($"Remove-OracleRegistryKeys: Deleted HKLM\\{fullKeyPath}.", "Rollback");
            return Task.FromResult(StepExecutionResult.Ok(
                $"Registry key HKLM\\{fullKeyPath} deleted."));
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Error("Remove-OracleRegistryKeys: Insufficient privileges.", ex, "Rollback");
            return Task.FromResult(StepExecutionResult.Fail(
                $"Cannot remove Oracle registry key HKLM\\{fullKeyPath} — Administrator privileges required. " +
                "Run WEDM as Administrator and retry, or delete the key manually via regedit.",
                1, ex, retryRecommended: false));
        }
        catch (Exception ex)
        {
            _log.Error("Remove-OracleRegistryKeys rollback failed.", ex, "Rollback");
            return Task.FromResult(StepExecutionResult.Fail(
                $"Failed to remove Oracle registry keys: {ex.Message}", 1, ex));
        }
    }
}
