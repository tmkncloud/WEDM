using System.ServiceProcess;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Remediation;

public sealed class OracleHomeSafetyAnalyzer : IOracleHomeSafetyAnalyzer
{
    private static readonly string[] OracleProcessNames =
        ["java", "javaw", "oui", "ouibean", "wlst", "nodemanager", "startWebLogic", "opatch"];

    private readonly IOracleProcessManager   _processes;
    private readonly IOracleInventoryService _inventory;

    public OracleHomeSafetyAnalyzer(IOracleProcessManager processes, IOracleInventoryService inventory)
    {
        _processes = processes;
        _inventory = inventory;
    }

    public SafetyAnalysisResult Analyze(RemediationDiscoveryContext context, OracleRemediationState classification)
    {
        var reasons  = new List<string>();
        var blocking = new List<string>();

        if (classification == OracleRemediationState.Healthy)
        {
            return new SafetyAnalysisResult
            {
                IsSafeToRemediate = false,
                Risk              = RemediationRiskLevel.Low,
                Confidence        = RemediationConfidence.High,
                Reasons           = ["No remediation required — Oracle home state is healthy."],
                Recommendation    = "Proceed with installation.",
            };
        }

        var mw = context.MiddlewareHome;
        var active = _processes.DetectMiddlewareProcesses();
        foreach (var proc in active)
        {
            if (ProcessTargetsHome(proc, mw))
            {
                blocking.Add($"Active process {proc.ProcessName} (PID {proc.ProcessId}) references middleware home.");
            }
        }

        var locks = _inventory.DetectLocks(context.OracleInventoryPath);
        foreach (var lk in locks.Where(l => !l.IsStale))
            blocking.Add($"Active inventory lock: {lk.LockFilePath}");

        if (_inventory.DetectHomeState(mw, context.OracleInventoryPath) == OracleHomeState.RegisteredAndPresent)
        {
            blocking.Add("Middleware home is registered in central inventory and appears complete — will not auto-delete.");
        }

        if (HasRecentInstallerActivity(context))
            blocking.Add("Recent installer activity detected under middleware home or temp directories.");

        foreach (var svc in FindOracleServicesUsingHome(mw))
            blocking.Add($"Windows service may be using home: {svc}");

        if (blocking.Count == 0)
            reasons.Add("No active Oracle processes, services, or inventory locks block cleanup.");

        if (classification is OracleRemediationState.PartialInstall or OracleRemediationState.FilesystemOnly)
            reasons.Add("Target is not registered as a complete Oracle home in central inventory.");

        var safe = blocking.Count == 0
                   && classification is OracleRemediationState.PartialInstall
                       or OracleRemediationState.FilesystemOnly
                       or OracleRemediationState.SafeToClean
                       or OracleRemediationState.StaleInventoryRegistration
                       or OracleRemediationState.Locked;

        var risk = blocking.Count > 0
            ? RemediationRiskLevel.High
            : classification == OracleRemediationState.Locked
                ? RemediationRiskLevel.Medium
                : RemediationRiskLevel.Low;

        return new SafetyAnalysisResult
        {
            IsSafeToRemediate = safe,
            Risk              = risk,
            Confidence        = blocking.Count == 0 ? RemediationConfidence.High : RemediationConfidence.Medium,
            Reasons           = reasons,
            BlockingReasons   = blocking,
            Recommendation    = safe
                ? "Automated safe cleanup can remove partial artifacts and continue deployment."
                : "Resolve blocking processes/services/locks before cleanup or use Remove WebLogic Environment.",
        };
    }

    private static bool ProcessTargetsHome(OracleProcessDescriptor proc, string middlewareHome)
    {
        if (string.IsNullOrWhiteSpace(proc.CommandLine))
            return false;
        return proc.CommandLine.Contains(middlewareHome, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName(name).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasRecentInstallerActivity(RemediationDiscoveryContext context)
    {
        var threshold = DateTime.UtcNow.AddMinutes(-15);
        var ouiMarkers = new[] { "oraInstall", "oui", "cfgtoollogs", ".lock", "install.platform" };

        foreach (var dir in new[] { context.MiddlewareHome, context.ExtractionDirectory, context.TempDirectory }
                     .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d!)))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir!, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (!ouiMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase)
                                             || file.Contains(m, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (new FileInfo(file).LastWriteTimeUtc >= threshold)
                        return true;
                }
            }
            catch
            {
                // ignore access errors
            }
        }

        return false;
    }

    private static List<string> FindOracleServicesUsingHome(string middlewareHome)
    {
        var found = new List<string>();
        try
        {
            foreach (var svc in ServiceController.GetServices())
            {
                if (!svc.ServiceName.Contains("oracle", StringComparison.OrdinalIgnoreCase)
                    && !svc.ServiceName.Contains("weblogic", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (svc.Status == ServiceControllerStatus.Running)
                    found.Add($"{svc.ServiceName} ({svc.Status})");
            }
        }
        catch
        {
            // ignore service enumeration failures
        }

        return found;
    }
}
