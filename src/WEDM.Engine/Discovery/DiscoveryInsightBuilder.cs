using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Engine.Discovery;

public static class DiscoveryInsightBuilder
{
    public static List<EnvironmentDiscoveryFinding> Build(
        MiddlewareTopologySnapshot topology,
        DomainAnalysisSnapshot domain,
        FormsReportsMetadataSnapshot forms,
        OracleInventorySnapshot inventory,
        IReadOnlyList<EnvironmentDiscoveryFinding> additional)
    {
        var findings = new List<EnvironmentDiscoveryFinding>(additional);

        if (!topology.NodeManagerConfigured)
        {
            findings.Add(Insight(
                CompatibilityRiskCategory.NodeManager,
                "Node Manager not configured",
                "Node Manager properties were not discovered for this domain.",
                CompatibilitySeverity.Medium));
        }

        if (!topology.SslEnabled)
        {
            findings.Add(Insight(
                CompatibilityRiskCategory.SecurityHardening,
                "Administration channel TLS not detected",
                "SSL/TLS listeners were not identified in domain configuration.",
                CompatibilitySeverity.Medium));
        }

        if (forms.UsesWebUtil)
        {
            findings.Add(Insight(
                CompatibilityRiskCategory.WebUtil,
                "WebUtil dependencies detected",
                $"{forms.WebUtilModuleCount} module(s) reference WebUtil in metadata scan.",
                CompatibilitySeverity.Medium));
        }

        if (forms.UsesOracleGraphics)
        {
            findings.Add(Insight(
                CompatibilityRiskCategory.ReportsRuntime,
                "Oracle Graphics artifacts detected",
                "OLB/graphics-related inventory found — review Reports modernization requirements.",
                CompatibilitySeverity.High));
        }

        if (inventory.InventoryState is OracleCentralInventoryState.Missing or OracleCentralInventoryState.Corrupted)
        {
            findings.Add(Insight(
                CompatibilityRiskCategory.General,
                "Oracle inventory incomplete",
                inventory.InventoryWarning ?? "oraInventory could not be fully resolved — patch analysis may be incomplete.",
                CompatibilitySeverity.Low));
        }

        if (topology.ManagedServerCount > 12)
        {
            findings.Add(Insight(
                CompatibilityRiskCategory.Topology,
                "Large managed server footprint",
                $"{topology.ManagedServerCount} managed servers increase migration orchestration complexity.",
                CompatibilitySeverity.Low));
        }

        if (domain.BootPropertiesPresent)
        {
            findings.Add(Insight(
                CompatibilityRiskCategory.SecurityHardening,
                "boot.properties present",
                "Domain uses boot.properties — verify credential store migration approach.",
                CompatibilitySeverity.Informational));
        }

        return findings;
    }

    private static EnvironmentDiscoveryFinding Insight(
        CompatibilityRiskCategory category,
        string title,
        string detail,
        CompatibilitySeverity severity) => new()
    {
        Category = category,
        Title    = title,
        Detail   = detail,
        Severity = severity,
    };
}
