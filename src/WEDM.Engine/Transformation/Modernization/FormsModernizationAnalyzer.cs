using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation.Modernization;

internal static class FormsModernizationAnalyzer
{
    public static FormsModernizationSnapshot Analyze(MigrationConfiguration config)
    {
        var snapshot = new FormsModernizationSnapshot();
        var formsHome = config.Source.FormsHome ?? config.FormsMetadata.ConfigurationPath;

        if (!string.IsNullOrWhiteSpace(formsHome) && Directory.Exists(formsHome))
        {
            var pll = Directory.EnumerateFiles(formsHome, "*.pll", SearchOption.AllDirectories)
                .Take(200).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList();
            var olb = Directory.EnumerateFiles(formsHome, "*.olb", SearchOption.AllDirectories)
                .Take(100).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList();

            snapshot.PllLibraries  = pll;
            snapshot.OlbReferences = olb;

            var modules = config.FormsMetadata.TopModules;
            foreach (var mod in modules.Take(25))
            {
                snapshot.ModuleDependencies.Add(new ModuleDependencyRecord
                {
                    ModuleName = mod,
                    DependsOn  = pll.Take(3).ToList(),
                });
            }
        }

        snapshot.TriggerSummary = $"Forms modules: {config.FormsMetadata.ModuleCount}, menus: {config.FormsMetadata.MenuCount}";
        snapshot.WebUtilClassification = config.FormsMetadata.UsesWebUtil
            ? $"WebUtil in use ({config.FormsMetadata.WebUtilModuleCount} modules) — client remediation required"
            : "No WebUtil usage detected";

        snapshot.ComplexityScore =
            config.FormsMetadata.FormCount / 10
            + config.FormsMetadata.CustomPlsqlLibraries
            + (config.FormsMetadata.UsesWebUtil ? 15 : 0)
            + (config.FormsMetadata.UsesOracleGraphics ? 20 : 0);

        if (config.FormsMetadata.UsesOracleGraphics)
            snapshot.Blockers.Add("Oracle Graphics objects require manual redesign before 12c+ target");

        if (config.FormsMetadata.UsesWebUtil)
            snapshot.ManualRemediationCandidates.Add("WebUtil client deployment and browser Java dependency");

        if (config.FormsMetadata.CustomPlsqlLibraries > 20)
            snapshot.ManualRemediationCandidates.Add($"Review {config.FormsMetadata.CustomPlsqlLibraries} custom PL/SQL libraries");

        foreach (var insight in config.DiscoveryInsights.Where(i => i.Category == Domain.Enums.CompatibilityRiskCategory.FormsConfiguration))
            snapshot.ManualRemediationCandidates.Add(insight.Title);

        return snapshot;
    }
}
