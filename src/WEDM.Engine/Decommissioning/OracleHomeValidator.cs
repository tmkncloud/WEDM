using System.Diagnostics;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Decommissioning;

public sealed class OracleHomeValidator : IOracleHomeValidator
{
    private readonly IOracleInventoryAnalyzer _inventory;
    private readonly IOracleProcessManager   _processes;

    public OracleHomeValidator(IOracleInventoryAnalyzer inventory, IOracleProcessManager processes)
    {
        _inventory = inventory;
        _processes = processes;
    }

    public OracleHomeValidationResult ValidateForInstall(DeploymentConfiguration config)
    {
        var checks = new List<string>();
        var blocking = new List<string>();
        var warnings = new List<string>();

        var mw = config.Paths.MiddlewareHome;
        var inv = config.Paths.OracleInventory;

        if (Directory.Exists(mw))
        {
            var wlserver = Path.Combine(mw, "wlserver");
            if (Directory.Exists(wlserver))
            {
                blocking.Add($"Middleware home already contains wlserver at {wlserver} — INST-07319 risk.");
                checks.Add($"FAIL: Partial or complete install detected at {mw}");
            }
            else
            {
                var entries = Directory.EnumerateFileSystemEntries(mw).Take(20).ToList();
                if (entries.Count > 0)
                {
                    warnings.Add($"Middleware home is not empty ({entries.Count} entries) — installer may reject location.");
                    checks.Add($"WARN: Non-empty middleware home: {mw}");
                }
            }
        }
        else
        {
            checks.Add($"PASS: Middleware home does not exist yet: {mw}");
        }

        var analysis = _inventory.Analyze(inv, mw);
        if (analysis.State == OracleCentralInventoryState.Missing)
        {
            blocking.Add("Central inventory.xml is missing — cannot install until inventory is initialized.");
            checks.Add("FAIL: Central inventory.xml missing.");
        }
        else if (analysis.State == OracleCentralInventoryState.Corrupted)
        {
            blocking.Add($"Central inventory.xml is corrupt or unreadable: {string.Join("; ", analysis.CorruptionWarnings)}");
            checks.Add("FAIL: Central inventory.xml corrupt.");
        }
        else if (analysis.State == OracleCentralInventoryState.Empty)
        {
            checks.Add("PASS: Central inventory is empty (clean-install state).");
        }

        if (analysis.LockPresent)
        {
            blocking.Add($"Inventory lock present at {analysis.LockFilePath}");
            checks.Add("FAIL: Oracle inventory lock files detected.");
        }

        foreach (var home in analysis.Homes.Where(h =>
            h.Path.Equals(mw, StringComparison.OrdinalIgnoreCase) ||
            h.Path.StartsWith(mw, StringComparison.OrdinalIgnoreCase)))
        {
            if (home.IsStale)
            {
                warnings.Add($"Stale inventory registration for {home.Path}");
                checks.Add($"WARN: Stale inventory entry for {home.Path}");
            }
            else
            {
                blocking.Add($"Oracle home already registered in inventory: {home.Path}");
                checks.Add($"FAIL: Inventory already registers target middleware path.");
            }
        }

        var active = _processes.DetectMiddlewareProcesses();
        if (active.Count > 0)
        {
            warnings.Add($"{active.Count} middleware process(es) still running.");
            checks.Add($"WARN: {active.Count} active middleware processes detected.");
        }

        if (IsRebootPending())
        {
            warnings.Add("Windows reports a pending reboot.");
            checks.Add("WARN: Pending reboot detected.");
        }

        return BuildResult(checks, blocking, warnings, rebootPending: false);
    }

    public OracleHomeValidationResult ValidateForRemoval(
        DecommissionConfiguration config,
        EnvironmentTopology? topology = null)
    {
        var checks = new List<string>();
        var blocking = new List<string>();
        var warnings = new List<string>();

        topology ??= new EnvironmentTopology();

        if (_processes.DetectMiddlewareProcesses().Count > 0)
        {
            warnings.Add("Middleware processes are still running — graceful shutdown recommended first.");
            checks.Add("WARN: Active middleware processes detected.");
        }

        foreach (var svc in topology.WindowsServices.Where(s => s.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"Windows service still running: {svc.ServiceName}");
            checks.Add($"WARN: Service running: {svc.ServiceName}");
        }

        try
        {
            var testFile = Path.Combine(config.Paths.TempDirectory, $"wedm_lock_test_{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(config.Paths.TempDirectory);
            File.WriteAllText(testFile, "x");
            File.Delete(testFile);
            checks.Add("PASS: Temp directory is writable.");
        }
        catch (Exception ex)
        {
            blocking.Add($"Cannot write to temp directory: {ex.Message}");
            checks.Add("FAIL: Temp directory not writable.");
        }

        return BuildResult(checks, blocking, warnings, rebootPending: IsRebootPending());
    }

    private static OracleHomeValidationResult BuildResult(
        List<string> checks,
        List<string> blocking,
        List<string> warnings,
        bool rebootPending)
    {
        return new OracleHomeValidationResult
        {
            Passed          = blocking.Count == 0,
            RebootRequired  = rebootPending,
            Checks          = checks,
            BlockingIssues  = blocking,
            Warnings        = warnings,
        };
    }

    private static bool IsRebootPending()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (key is not null) return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }
}
