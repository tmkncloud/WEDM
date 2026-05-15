using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation.Transformers;

internal static class DomainConfigPrepTransformer
{
    public static ConfigTransformationRecord? Transform(MigrationConfiguration config)
    {
        var domainHome = config.Source.DomainHome;
        if (string.IsNullOrWhiteSpace(domainHome)) return null;

        var configXml = Path.Combine(domainHome, "config", "config.xml");
        if (!File.Exists(configXml)) return null;

        var original = TransformationSafeIO.ReadSourceFileSafe(configXml, 256_000);
        var notes = new List<string>
        {
            $"Domain '{config.Topology.DomainName}' — {config.Topology.ManagedServerCount} managed servers, {config.Topology.ClusterCount} clusters",
            $"JDBC resources detected: {config.DomainAnalysis.JdbcResourceCount}",
            "Target domain recreation should use generated WLST scripts — do not copy config.xml directly across major versions",
        };

        if (!config.Topology.SslEnabled)
            notes.Add("Plan TLS enablement on admin and managed server channels before production cutover");

        return new ConfigTransformationRecord
        {
            SourcePath      = configXml,
            OutputPath      = $"{MigrationWorkspaceManager.ConfigsDir}/config-migration-notes.md",
            Summary         = "config.xml migration preparation (analysis only — no in-place overwrite)",
            OriginalExcerpt = Truncate(original, 1500),
            TransformedExcerpt = "# Domain config migration preparation\n\n" + string.Join("\n", notes.Select(n => "- " + n)),
            RemediationNotes = notes,
        };
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\n... [truncated]";
}
