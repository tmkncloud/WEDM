using System.Diagnostics;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Installer;

/// <summary>
/// Validates that the environment is clean enough for an OUI installer attempt to have a
/// reasonable chance of success.
///
/// Checks (all non-fatal — results are logged; callers decide whether to abort):
///   1. Oracle Home state — must not be RegisteredAndPresent or PartialInstall
///   2. Inventory locks — no active (non-stale) lock files
///   3. Orphan installer processes — no hanging java.exe / OraInstall* processes
///   4. Temp directory writability — isolated temp dir must accept new files
///   5. Disk space — temp and MW parent drive must have at least MinDiskSpaceMb free
///
/// Remediation actions taken automatically (non-destructive):
///   • Stale lock files (age &gt; 4h) are reported but not auto-deleted (safety)
///   • OraInstall* extraction caches from previous attempts are purged
/// </summary>
public sealed class InstallerRetryPreflight
{
    private const long MinDiskSpaceMb = 2048L;  // 2 GiB minimum free

    private readonly IOracleInventoryService _inventory;
    private readonly ILoggingService         _log;

    public InstallerRetryPreflight(IOracleInventoryService inventory, ILoggingService log)
    {
        _inventory = inventory;
        _log       = log;
    }

    /// <summary>
    /// Validates the environment before an OUI attempt and returns a structured result.
    /// All cleanup performed is captured in <see cref="InstallerRetryPreflightResult.ActionsTaken"/>.
    /// </summary>
    public InstallerRetryPreflightResult Validate(
        DeploymentConfiguration config,
        string                  stepName,
        int                     attemptNumber)
    {
        _log.Info(
            $"[InstallerPreflight] Starting pre-flight for step '{stepName}' attempt {attemptNumber}.",
            "Installer.Preflight");

        var findings  = new List<string>();
        var blocking  = new List<string>();
        var actions   = new List<string>();

        // ── 1. Oracle Home state ──────────────────────────────────────────────
        CheckOracleHomeState(config, findings, blocking);

        // ── 2. Inventory lock check ───────────────────────────────────────────
        CheckInventoryLocks(config, findings, blocking);

        // ── 3. Orphan installer processes ─────────────────────────────────────
        CheckOrphanProcesses(findings, blocking);

        // ── 4. Temp directory writability ─────────────────────────────────────
        CheckTempDirectory(config, findings, blocking);

        // ── 5. Disk space ─────────────────────────────────────────────────────
        CheckDiskSpace(config, findings, blocking);

        // ── 6. Purge stale OraInstall* extraction caches ─────────────────────
        PurgeExtractionCaches(config, actions);

        bool canProceed = blocking.Count == 0;

        _log.Info(
            $"[InstallerPreflight] Step '{stepName}' attempt {attemptNumber}: " +
            $"canProceed={canProceed} checks={findings.Count} blocking={blocking.Count} actions={actions.Count}",
            "Installer.Preflight");

        if (!canProceed)
            foreach (var b in blocking)
                _log.Warning($"[InstallerPreflight] BLOCKING: {b}", "Installer.Preflight");

        return new InstallerRetryPreflightResult
        {
            CanProceed    = canProceed,
            Findings      = findings.AsReadOnly(),
            BlockingItems = blocking.AsReadOnly(),
            ActionsTaken  = actions.AsReadOnly(),
        };
    }

    // ── Private checks ────────────────────────────────────────────────────────

    private void CheckOracleHomeState(
        DeploymentConfiguration config,
        List<string> findings,
        List<string> blocking)
    {
        try
        {
            var state = _inventory.DetectHomeState(
                config.Paths.MiddlewareHome,
                config.Paths.OracleInventory);

            findings.Add($"Oracle Home state: {state}");

            switch (state)
            {
                case OracleHomeState.RegisteredAndPresent:
                    blocking.Add(
                        $"MW Home '{config.Paths.MiddlewareHome}' is registered AND present. " +
                        "Run WEDM rollback (Remove-MiddlewareHome + Remove-OracleFolders) before retrying.");
                    break;

                case OracleHomeState.PartialInstall:
                    blocking.Add(
                        $"MW Home '{config.Paths.MiddlewareHome}' has partial OUI artifacts. " +
                        "Delete the directory and remove the inventory entry before retrying.");
                    break;

                case OracleHomeState.InventoryLocked:
                    // Also caught by lock check below, but surface here too for clarity
                    blocking.Add(
                        $"Oracle inventory is locked — another OUI/OPatch session may be active.");
                    break;

                case OracleHomeState.RegisteredOrphaned:
                    findings.Add(
                        $"  Orphaned inventory registration found for '{config.Paths.MiddlewareHome}'. " +
                        "WEDM will remove it before OUI launch (via pre-install validation).");
                    break;

                case OracleHomeState.Clean:
                    findings.Add("  Oracle Home state is Clean ✔");
                    break;

                default:
                    findings.Add($"  State '{state}' is acceptable for retry.");
                    break;
            }
        }
        catch (Exception ex)
        {
            findings.Add($"Oracle Home state check failed: {ex.Message}");
            _log.Warning($"[InstallerPreflight] Oracle Home state check threw: {ex.Message}", "Installer.Preflight");
        }
    }

    private void CheckInventoryLocks(
        DeploymentConfiguration config,
        List<string> findings,
        List<string> blocking)
    {
        try
        {
            var locks = _inventory.DetectLocks(config.Paths.OracleInventory);
            if (locks.Count == 0)
            {
                findings.Add("Inventory locks: none ✔");
                return;
            }

            var activeLocks = locks.Where(l => !l.IsStale).ToList();
            var staleLocks  = locks.Where(l => l.IsStale).ToList();

            if (staleLocks.Count > 0)
                findings.Add($"  {staleLocks.Count} stale lock(s) found (age > 4h) — treated as inactive.");

            if (activeLocks.Count > 0)
            {
                blocking.Add(
                    $"Oracle inventory has {activeLocks.Count} active lock file(s). " +
                    "Wait for the active OUI/OPatch operation to finish, then retry.");
                foreach (var lk in activeLocks)
                    findings.Add($"  Active lock: {lk.LockFilePath} (modified: {lk.LastModified:u})");
            }
        }
        catch (Exception ex)
        {
            findings.Add($"Lock check skipped: {ex.Message}");
        }
    }

    private static void CheckOrphanProcesses(List<string> findings, List<string> blocking)
    {
        var orphans = FindOrphanInstallerProcesses();
        if (orphans.Count == 0)
        {
            findings.Add("Orphan installer processes: none ✔");
            return;
        }

        blocking.Add(
            $"Orphan installer processes detected ({orphans.Count}). " +
            "Kill them before retrying to prevent resource conflicts.");
        foreach (var p in orphans)
            findings.Add($"  Orphan process: {p}");
    }

    private static void CheckTempDirectory(
        DeploymentConfiguration config,
        List<string> findings,
        List<string> blocking)
    {
        var tmpPath = config.Paths.TempDirectory;
        try
        {
            Directory.CreateDirectory(tmpPath);
            var probe = Path.Combine(tmpPath, $"wedm_preflight_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "preflight");
            File.Delete(probe);
            findings.Add($"Temp directory writable: {tmpPath} ✔");
        }
        catch (Exception ex)
        {
            blocking.Add($"Temp directory '{tmpPath}' is not writable: {ex.Message}");
        }
    }

    private static void CheckDiskSpace(
        DeploymentConfiguration config,
        List<string> findings,
        List<string> blocking)
    {
        var pathsToCheck = new[]
        {
            (config.Paths.TempDirectory,    "Temp"),
            (config.Paths.MiddlewareHome,   "MiddlewareHome"),
        };

        foreach (var (path, label) in pathsToCheck)
        {
            try
            {
                // Resolve to the root drive — walk up until we find an existing dir
                var existing = path;
                while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing))
                    existing = Path.GetDirectoryName(existing) ?? string.Empty;

                if (string.IsNullOrEmpty(existing)) continue;

                var drive = new DriveInfo(Path.GetPathRoot(existing) ?? existing);
                var freeMb = drive.AvailableFreeSpace / (1024 * 1024);
                findings.Add($"Disk space [{label}] drive '{drive.Name}': {freeMb:N0} MB free");

                if (freeMb < MinDiskSpaceMb)
                    blocking.Add(
                        $"Insufficient disk space on drive '{drive.Name}' for {label}: " +
                        $"{freeMb:N0} MB free, {MinDiskSpaceMb:N0} MB required.");
            }
            catch (Exception ex)
            {
                findings.Add($"Disk space check skipped for {label}: {ex.Message}");
            }
        }
    }

    private static void PurgeExtractionCaches(
        DeploymentConfiguration config,
        List<string> actions)
    {
        // OUI extracts its JAR to OraInstall* directories under the Java temp dir.
        // Purge any that exist from previous failed attempts in the configured temp root.
        var dirsToSearch = new[] { config.Paths.TempDirectory, Path.GetTempPath() };

        foreach (var root in dirsToSearch.Where(Directory.Exists).Distinct())
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root, "OraInstall*",
                    SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        actions.Add($"Purged OUI extraction cache: {dir}");
                    }
                    catch (Exception ex)
                    {
                        actions.Add($"Could not purge {dir} (locked?): {ex.Message}");
                    }
                }
            }
            catch { /* non-fatal — best effort */ }
        }
    }

    private static List<string> FindOrphanInstallerProcesses()
    {
        var result = new List<string>();
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var name = proc.ProcessName;
                    // Match OUI-specific process names only (avoid generic java.exe false positives
                    // from other JVM processes on the machine by checking the command line if possible)
                    if (name.StartsWith("OraInstall", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("oui", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add($"{name} (PID {proc.Id})");
                        continue;
                    }

                    // For java.exe, try to read the main module path to narrow to OUI instances
                    if (name.Equals("java", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("javaw", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var cmdLine = GetCommandLine(proc);
                            if (cmdLine is not null &&
                                (cmdLine.Contains("OraInstall", StringComparison.OrdinalIgnoreCase) ||
                                 cmdLine.Contains("oui.jar", StringComparison.OrdinalIgnoreCase) ||
                                 cmdLine.Contains("fmw_", StringComparison.OrdinalIgnoreCase)))
                            {
                                result.Add($"{name} (PID {proc.Id}) [OUI-related java]");
                            }
                        }
                        catch { /* can't read command line — skip */ }
                    }
                }
                catch { /* process may have exited */ }
            }
        }
        catch { /* GetProcesses failed — non-fatal */ }
        return result;
    }

    /// <summary>
    /// Attempts to read the command line of a process via WMI (best-effort, Windows-only).
    /// Returns null on any failure.
    /// </summary>
    private static string? GetCommandLine(Process process)
    {
        try
        {
            // Avoid WMI overhead when running under unit tests or non-admin context
            // by reading the first module path instead.
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
