using System.Diagnostics;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Migration;
using WEDM.Domain.Models;

namespace WEDM.Engine.Migration;

/// <summary>
/// Enterprise compatibility assessment engine — weighted scoring, executive summaries, and findings register.
/// </summary>
public sealed class CompatibilityAssessmentEngine : ICompatibilityAssessmentEngine
{
    private List<CompatibilityFinding> _lastFindings = [];

    public IReadOnlyList<CompatibilityFinding> GetLastFindings() => _lastFindings;

    public Task<MigrationReadinessSnapshot> AssessAsync(
        MigrationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        _lastFindings = BuildFindings(configuration);
        configuration.CompatibilityFindings = _lastFindings.ToList();

        var snapshot = MigrationReadinessScorer.Score(configuration, _lastFindings);
        configuration.Readiness = snapshot;
        configuration.AssessmentCompleted = true;
        configuration.AssessmentDurationMs = sw.ElapsedMilliseconds;

        return Task.FromResult(snapshot);
    }

    private static List<CompatibilityFinding> BuildFindings(MigrationConfiguration config)
    {
        var findings = new List<CompatibilityFinding>();
        var source = config.Source.Release;
        var target = config.Target.Release;

        foreach (var insight in config.DiscoveryInsights.Where(i => i.Severity >= CompatibilitySeverity.Medium))
        {
            findings.Add(Finding(
                insight.Title,
                insight.Detail,
                insight.Severity,
                insight.Category,
                "Address during environment discovery remediation window.",
                blocksMigration: insight.Severity == CompatibilitySeverity.Critical));
        }

        if (!config.DiscoveryUsedRealScan && config.DiscoveryCompleted)
        {
            findings.Add(Finding(
                "Discovery used assessment preview",
                "Middleware paths were inaccessible — compatibility scoring includes conservative assumptions. Re-run discovery with valid paths before cutover planning.",
                CompatibilitySeverity.Medium,
                CompatibilityRiskCategory.General,
                "Provide accessible middleware and domain home paths, then re-run discovery.",
                blocksMigration: false));
        }

        if (config.DomainAnalysis.DeprecatedJvmFlags.Count > 0)
        {
            findings.Add(Finding(
                "Deprecated JVM flags in startup scripts",
                string.Join(", ", config.DomainAnalysis.DeprecatedJvmFlags.Take(6)),
                CompatibilitySeverity.High,
                CompatibilityRiskCategory.JvmConfiguration,
                "Update setDomainEnv / managed server arguments for target JDK tier.",
                blocksMigration: false));
        }

        if (!config.OracleInventory.InventoryHealthy)
        {
            findings.Add(Finding(
                "Oracle central inventory incomplete",
                config.OracleInventory.InventoryWarning ?? "inventory.xml could not be parsed or is missing registered homes.",
                CompatibilitySeverity.Medium,
                CompatibilityRiskCategory.General,
                "Verify oraInst.loc and central inventory permissions.",
                blocksMigration: false));
        }

        if (config.OracleInventory.Patches.Count > 40)
        {
            findings.Add(Finding(
                "Large patch inventory footprint",
                $"{config.OracleInventory.Patches.Count} patches detected — plan extended regression and OPatch staging for target stack.",
                CompatibilitySeverity.Low,
                CompatibilityRiskCategory.General,
                "Export OPatch inventory and map required target patches before migration window.",
                blocksMigration: false));
        }

        if (config.Topology.ManagedServerCount > 8)
        {
            findings.Add(Finding(
                "High managed server count",
                $"{config.Topology.ManagedServerCount} managed servers increase cutover coordination and configuration drift risk.",
                CompatibilitySeverity.Low,
                CompatibilityRiskCategory.General,
                "Group servers by cluster and application tier for phased migration.",
                blocksMigration: false));
        }

        if (source == MiddlewareReleaseKind.Forms6i)
        {
            findings.Add(Finding(
                "Legacy JVM arguments detected",
                "Domain startup scripts reference PermGen and CMS garbage collector flags incompatible with supported JDK tiers on 11g–14c.",
                CompatibilitySeverity.High,
                CompatibilityRiskCategory.JvmConfiguration,
                "Regenerate managed server startup arguments during target domain provisioning.",
                blocksMigration: false));

            findings.Add(Finding(
                "WebUtil client dependency",
                $"{config.FormsMetadata.WebUtilModuleCount} module(s) reference WebUtil — plan browser and Java dependency remediation for 12c/14c.",
                CompatibilitySeverity.Medium,
                CompatibilityRiskCategory.WebUtil,
                "Inventory WebUtil triggers and schedule client deployment updates.",
                blocksMigration: false));
        }

        if (source is MiddlewareReleaseKind.Forms6i or MiddlewareReleaseKind.Forms10g)
        {
            findings.Add(Finding(
                "Legacy authentication integration",
                "OID / SSO configuration patterns must be re-mapped for modern OAM, SAML, or cloud identity providers.",
                CompatibilitySeverity.High,
                CompatibilityRiskCategory.Authentication,
                "Document identity store bindings and certificate trust chains before cutover.",
                blocksMigration: false));
        }

        if (config.FormsMetadata.UsesOracleGraphics)
        {
            findings.Add(Finding(
                "Oracle Graphics runtime dependency",
                "Graphics-based Reports objects are not supported on Fusion Middleware 12c+ without redesign.",
                CompatibilitySeverity.Critical,
                CompatibilityRiskCategory.ReportsRuntime,
                "Replace Graphics charts with RDF or BI Publisher equivalents.",
                blocksMigration: true));
        }

        if (!config.Topology.NodeManagerConfigured && target >= MiddlewareReleaseKind.Forms12c)
        {
            findings.Add(Finding(
                "Node Manager enrollment required",
                "Target 12c/14c production automation expects Node Manager on all managed server hosts.",
                CompatibilitySeverity.Medium,
                CompatibilityRiskCategory.NodeManager,
                "Plan nmEnroll, machine mapping, and Windows service registration.",
                blocksMigration: false));
        }

        if (!config.Topology.SslEnabled)
        {
            findings.Add(Finding(
                "Administration channel not TLS-enabled",
                "Administration traffic should use TLS before production cutover to meet enterprise security baselines.",
                CompatibilitySeverity.Medium,
                CompatibilityRiskCategory.SecurityHardening,
                "Enable SSL on admin and managed server channels during domain recreation.",
                blocksMigration: false));
        }

        if (config.FormsMetadata.CustomPlsqlLibraries > 15)
        {
            findings.Add(Finding(
                "Elevated custom PL/SQL library footprint",
                $"{config.FormsMetadata.CustomPlsqlLibraries} custom PL/SQL libraries expand regression and performance test scope.",
                CompatibilitySeverity.Low,
                CompatibilityRiskCategory.FormsConfiguration,
                "Prioritize libraries referenced by revenue-critical modules.",
                blocksMigration: false));
        }

        findings.Add(Finding(
            $"Recommended upgrade path: {MigrationVersionMatrix.DescribeUpgradePath(source, target)}",
            "WEDM orchestrates middleware provisioning, configuration transformation, and validation gates for this modernization path.",
            CompatibilitySeverity.Informational,
            CompatibilityRiskCategory.General,
            remediation: null,
            blocksMigration: false));

        if (target == MiddlewareReleaseKind.Forms14c)
        {
            findings.Add(Finding(
                "JDK 21 platform requirement",
                "Fusion Middleware 14c requires JDK 21 LTS — validate Forms client, batch, and integration JVM compatibility.",
                CompatibilitySeverity.Medium,
                CompatibilityRiskCategory.JvmConfiguration,
                "Align JDK rollout with WebLogic 14c provisioning.",
                blocksMigration: false));
        }

        return findings;
    }

    private static CompatibilityFinding Finding(
        string title,
        string description,
        CompatibilitySeverity severity,
        CompatibilityRiskCategory category,
        string? remediation,
        bool blocksMigration) => new()
    {
        Title = title,
        Description = description,
        Severity = severity,
        Category = category,
        Remediation = remediation,
        BlocksMigration = blocksMigration,
    };
}
