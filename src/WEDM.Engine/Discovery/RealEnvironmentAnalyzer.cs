using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;

namespace WEDM.Engine.Discovery;

/// <summary>Derives compatibility findings from real discovered environment data.</summary>
public static class RealEnvironmentAnalyzer
{
    public static List<EnvironmentDiscoveryFinding> Analyze(
        MiddlewareTopologySnapshot topology,
        DomainAnalysisSnapshot domain,
        FormsReportsMetadataSnapshot forms,
        OracleInventorySnapshot inventory)
    {
        var findings = new List<EnvironmentDiscoveryFinding>();
        findings.AddRange(JvmStartupAnalyzer.AnalyzeDeprecatedArgs(topology.JvmArguments));

        foreach (var arg in topology.JvmArguments)
        {
            if (arg.Contains("TLSv1", StringComparison.OrdinalIgnoreCase)
                && !arg.Contains("TLSv1.2", StringComparison.OrdinalIgnoreCase)
                && !arg.Contains("TLSv1.3", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    CompatibilityRiskCategory.SecurityHardening,
                    "Legacy TLS protocol reference",
                    $"Startup argument '{arg}' suggests legacy TLS configuration.",
                    CompatibilitySeverity.Medium));
            }
        }

        if (domain.NodeManagerSecure == false)
        {
            findings.Add(Finding(
                CompatibilityRiskCategory.NodeManager,
                "Node Manager non-SSL listener",
                "Node Manager secure listener is not enabled.",
                CompatibilitySeverity.Medium));
        }

        if (inventory.Patches.Count == 0 && !string.IsNullOrWhiteSpace(inventory.OpatchVersion))
        {
            findings.Add(Finding(
                CompatibilityRiskCategory.General,
                "No patches reported by OPatch inventory",
                "OPatch is present but returned an empty patch inventory — verify ORACLE_HOME.",
                CompatibilitySeverity.Informational));
        }

        if (forms.FormCount > 400)
        {
            findings.Add(Finding(
                CompatibilityRiskCategory.FormsConfiguration,
                "Large Forms module inventory",
                $"{forms.FormCount} form modules increase regression testing scope.",
                CompatibilitySeverity.Low));
        }

        return findings;
    }

    private static EnvironmentDiscoveryFinding Finding(
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
