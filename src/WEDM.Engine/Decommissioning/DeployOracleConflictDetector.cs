using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.OracleInventory;

namespace WEDM.Engine.Decommissioning;

/// <summary>
/// Pre-flight Oracle conflict detection for deploy mode (stale inventory, partial installs, home reuse).
/// </summary>
public sealed class DeployOracleConflictDetector : IDeployOracleConflictDetector
{
    private readonly IOracleInventoryAnalyzer _inventory;
    private readonly IOracleHomeValidator    _homeValidator;

    public DeployOracleConflictDetector(IOracleInventoryAnalyzer inventory, IOracleHomeValidator homeValidator)
    {
        _inventory    = inventory;
        _homeValidator = homeValidator;
    }

    public OracleConflictReport DetectConflicts(DeploymentConfiguration config)
    {
        var findings = new List<OracleConflictFinding>();
        var validation = _homeValidator.ValidateForInstall(config);
        var analysis   = _inventory.Analyze(config.Paths.OracleInventory, config.Paths.MiddlewareHome);

        foreach (var issue in validation.BlockingIssues)
        {
            findings.Add(new OracleConflictFinding
            {
                Code       = "OracleHome.Conflict",
                Severity   = OracleConflictSeverity.Blocking,
                Message    = issue,
                Remediation = config.OracleLifecycle.SuggestDecommissionOnConflict
                    ? "Run Remove WebLogic Environment to decommission and sanitize Oracle state, then redeploy."
                    : "Clear middleware home remnants and detach stale inventory registrations.",
                Path = config.Paths.MiddlewareHome,
            });
        }

        foreach (var warn in validation.Warnings)
        {
            findings.Add(new OracleConflictFinding
            {
                Code     = "OracleHome.Warning",
                Severity = OracleConflictSeverity.Warning,
                Message  = warn,
                Path     = config.Paths.MiddlewareHome,
            });
        }

        AddInventoryStateFindings(findings, analysis);

        var hasBlocking = findings.Any(f => f.Severity == OracleConflictSeverity.Blocking);
        var suggestDecommission = hasBlocking && config.OracleLifecycle.SuggestDecommissionOnConflict;

        return new OracleConflictReport
        {
            HasBlockingConflicts       = hasBlocking,
            SuggestDecommission        = suggestDecommission,
            ForceCleanInstallRecommended = hasBlocking || findings.Any(f => f.Code.StartsWith("OracleHome", StringComparison.Ordinal)),
            Findings                   = findings,
        };
    }

    private static void AddInventoryStateFindings(List<OracleConflictFinding> findings, OracleInventoryAnalysis analysis)
    {
        switch (analysis.State)
        {
            case OracleCentralInventoryState.Missing:
                findings.Add(new OracleConflictFinding
                {
                    Code        = "Inventory.Missing",
                    Severity    = OracleConflictSeverity.Error,
                    Message     = string.Join("; ", analysis.CorruptionWarnings),
                    Remediation = "Create or restore ContentsXML/inventory.xml under the Oracle central inventory directory.",
                    Path        = analysis.InventoryRoot,
                });
                break;

            case OracleCentralInventoryState.Corrupted:
                findings.Add(new OracleConflictFinding
                {
                    Code        = "Inventory.Corrupt",
                    Severity    = OracleConflictSeverity.Error,
                    Message     = string.Join("; ", analysis.CorruptionWarnings),
                    Remediation = "Repair or regenerate central inventory.xml before install.",
                    Path        = analysis.InventoryRoot,
                });
                break;

            case OracleCentralInventoryState.Empty:
                findings.Add(new OracleConflictFinding
                {
                    Code     = "Inventory.Empty",
                    Severity = OracleConflictSeverity.Informational,
                    Message  = OracleCentralInventoryClassifier.EmptyInventoryMessage,
                    Path     = analysis.InventoryRoot,
                });
                break;

            case OracleCentralInventoryState.Locked:
                findings.Add(new OracleConflictFinding
                {
                    Code        = "Inventory.Lock",
                    Severity    = OracleConflictSeverity.Blocking,
                    Message     = string.Join("; ", analysis.CorruptionWarnings),
                    Remediation = "Stop OUI/installer processes and clear inventory locks before retrying.",
                    Path        = analysis.LockFilePath ?? analysis.InventoryRoot,
                });
                break;
        }

        foreach (var stale in analysis.Homes.Where(h => h.IsStale))
        {
            findings.Add(new OracleConflictFinding
            {
                Code        = "Inventory.StaleHome",
                Severity    = OracleConflictSeverity.Warning,
                Message     = $"Stale inventory registration: {stale.Path}",
                Remediation = "Detach stale home from oraInventory or run Remove Environment.",
                Path        = stale.Path,
            });
        }
    }
}
