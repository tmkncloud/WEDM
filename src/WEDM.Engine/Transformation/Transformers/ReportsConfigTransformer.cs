using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation.Transformers;

internal static class ReportsConfigTransformer
{
    public static ConfigTransformationRecord? Transform(MigrationConfiguration config)
    {
        var reportsHome = config.Source.ReportsHome;
        if (string.IsNullOrWhiteSpace(reportsHome) || !Directory.Exists(reportsHome)) return null;

        var cfg = Directory.EnumerateFiles(reportsHome, "rwbuilder.conf", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(reportsHome, "reports.sh", SearchOption.AllDirectories))
            .FirstOrDefault();

        if (cfg is null)
        {
            return new ConfigTransformationRecord
            {
                SourcePath      = reportsHome,
                OutputPath      = $"{MigrationWorkspaceManager.ConfigsDir}/reports-modernization-notes.md",
                Summary         = "Reports server modernization preparation",
                TransformedExcerpt = "# Reports modernization\n\n- Inventory Reports servers and output destinations\n- Validate RDF/REP compatibility on target tier",
                RemediationNotes = ["No standard Reports config file found — manual inventory required"],
            };
        }

        var original = TransformationSafeIO.ReadSourceFileSafe(cfg);
        return new ConfigTransformationRecord
        {
            SourcePath         = cfg,
            OutputPath         = $"{MigrationWorkspaceManager.ConfigsDir}/reports-config-notes.md",
            Summary            = "Reports configuration modernization preparation",
            OriginalExcerpt    = Truncate(original, 800),
            TransformedExcerpt = "# Review Reports runtime paths and printer/email destinations for target environment",
            RemediationNotes   = ["Validate Reports server classpath and ORACLE_INSTANCE mapping"],
        };
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\n... [truncated]";
}
