using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.OracleInventory;
using WEDM.Engine.Remediation;

namespace WEDM.Engine.Decommissioning;

/// <summary>
/// Pre-flight Oracle conflict detection for deploy mode (stale inventory, partial installs, home reuse).
/// </summary>
public sealed class DeployOracleConflictDetector : IDeployOracleConflictDetector
{
    private readonly IOracleInventoryAnalyzer _inventory;
    private readonly IOracleHomeValidator    _homeValidator;
    private readonly IOracleRemediationService? _remediation;
    private readonly IOracleInventoryBootstrapService? _bootstrap;

    public DeployOracleConflictDetector(
        IOracleInventoryAnalyzer inventory,
        IOracleHomeValidator homeValidator,
        IOracleRemediationService? remediation = null,
        IOracleInventoryBootstrapService? bootstrap = null)
    {
        _inventory    = inventory;
        _homeValidator = homeValidator;
        _remediation  = remediation;
        _bootstrap    = bootstrap;
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
        AddRemediationFindings(findings, config);
        AddBootstrapFindings(findings, config, analysis);

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
                    Remediation = "Configure Oracle inventory path or enable automatic inventory bootstrap.",
                    Path        = analysis.InventoryRoot,
                });
                break;

            case OracleCentralInventoryState.BootstrapRequired:
                findings.Add(new OracleConflictFinding
                {
                    Code        = "Inventory.BootstrapRequired",
                    Severity    = OracleConflictSeverity.Warning,
                    Message     = string.Join("; ", analysis.CorruptionWarnings),
                    Remediation = "WEDM can initialize a clean central inventory automatically when bootstrap is enabled.",
                    Path        = analysis.InventoryRoot,
                });
                break;

            case OracleCentralInventoryState.BootstrapFailed:
                findings.Add(new OracleConflictFinding
                {
                    Code        = "Inventory.BootstrapFailed",
                    Severity    = OracleConflictSeverity.Error,
                    Message     = string.Join("; ", analysis.CorruptionWarnings),
                    Remediation = "Review bootstrap report and repair inventory before continuing.",
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

    private void AddBootstrapFindings(
        List<OracleConflictFinding> findings,
        DeploymentConfiguration config,
        OracleInventoryAnalysis analysis)
    {
        if (_bootstrap is null || analysis.State != OracleCentralInventoryState.BootstrapRequired)
            return;

        var assessment = _bootstrap.Assess(config);
        if (config.OracleLifecycle.EnableAutomaticInventoryBootstrap && assessment.CanAutoBootstrap)
        {
            findings.Add(new OracleConflictFinding
            {
                Code     = "Inventory.BootstrapAvailable",
                Severity = OracleConflictSeverity.Informational,
                Message  = "Automatic Oracle central inventory bootstrap is available for this clean install.",
                Path     = analysis.InventoryRoot,
            });
        }
    }

    private void AddRemediationFindings(List<OracleConflictFinding> findings, DeploymentConfiguration config)
    {
        if (_remediation is null)
            return;

        var assessment = _remediation.Assess(config, "ValidatePrerequisites");
        if (!assessment.RequiresRemediation)
            return;

        var plan = assessment.RecommendedPlan;
        var actionCount = plan?.Actions.Count ?? 0;
        var severity = assessment.CanAutoRemediate && config.OracleLifecycle.EnableAutoRemediation
            ? OracleConflictSeverity.Warning
            : OracleConflictSeverity.Warning;

        findings.Add(new OracleConflictFinding
        {
            Code        = "Remediation.PartialInstall",
            Severity    = severity,
            Message     = $"{assessment.Classification}: {assessment.Issues.FirstOrDefault()?.Message ?? "Oracle state requires cleanup before install."} " +
                          $"({actionCount} automated action(s) available, confidence={assessment.Safety.Confidence}).",
            Remediation = assessment.Safety.Recommendation,
            Path        = config.Paths.MiddlewareHome,
        });

        if (config.OracleLifecycle.EnableAutoRemediation
            && config.OracleLifecycle.AutoRemediationMode == AutoRemediationMode.AutomaticSafeOnly
            && assessment.CanAutoRemediate)
        {
            findings.Add(new OracleConflictFinding
            {
                Code     = "Remediation.AutoRepairAvailable",
                Severity = OracleConflictSeverity.Informational,
                Message  = "Safe auto-repair is available during InstallInfrastructure if pre-check blocks OUI.",
                Path     = config.Paths.MiddlewareHome,
            });
        }
    }
}
