using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation;

internal static class RemediationRecommendationBuilder
{
    public static List<RemediationRecommendation> Build(MigrationConfiguration config, TransformationExecutionResult result)
    {
        var items = new List<RemediationRecommendation>();

        foreach (var finding in config.CompatibilityFindings.Where(f => f.Severity >= CompatibilitySeverity.Medium))
        {
            items.Add(new RemediationRecommendation
            {
                Title           = finding.Title,
                Detail          = finding.Description,
                Category        = finding.Category,
                Severity        = finding.Severity,
                ManualRequired  = finding.BlocksMigration || finding.Severity >= CompatibilitySeverity.High,
                EstimatedEffortHours = EstimateHours(finding.Severity),
            });
        }

        foreach (var blocker in result.FormsModernization.Blockers)
        {
            items.Add(new RemediationRecommendation
            {
                Title          = blocker,
                Detail         = "Forms modernization blocker — manual remediation before target cutover",
                Category       = CompatibilityRiskCategory.FormsConfiguration,
                Severity       = CompatibilitySeverity.Critical,
                ManualRequired = true,
                EstimatedEffortHours = 40,
            });
        }

        foreach (var feature in result.ReportsModernization.UnsupportedFeatures)
        {
            items.Add(new RemediationRecommendation
            {
                Title          = $"Reports: {feature}",
                Detail         = "Unsupported Reports feature on target tier",
                Category       = CompatibilityRiskCategory.ReportsRuntime,
                Severity       = CompatibilitySeverity.High,
                ManualRequired = true,
                EstimatedEffortHours = 24,
            });
        }

        foreach (var ct in result.ConfigTransformations)
        {
            foreach (var note in ct.RemediationNotes)
            {
                if (items.Any(i => i.Title == note)) continue;
                items.Add(new RemediationRecommendation
                {
                    Title          = note,
                    Detail         = ct.Summary,
                    Category       = CompatibilityRiskCategory.General,
                    Severity       = CompatibilitySeverity.Medium,
                    ManualRequired = false,
                    EstimatedEffortHours = 4,
                });
            }
        }

        return items.DistinctBy(i => i.Title).ToList();
    }

    private static double EstimateHours(CompatibilitySeverity severity) => severity switch
    {
        CompatibilitySeverity.Critical => 32,
        CompatibilitySeverity.High     => 16,
        CompatibilitySeverity.Medium   => 8,
        CompatibilitySeverity.Low        => 4,
        _                              => 2,
    };
}
