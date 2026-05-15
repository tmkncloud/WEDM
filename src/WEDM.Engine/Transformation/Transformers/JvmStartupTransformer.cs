using System.Text.RegularExpressions;
using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation.Transformers;

internal static class JvmStartupTransformer
{
    private static readonly (string Pattern, string Replacement, string Note)[] Replacements =
    [
        ("-XX:PermSize=\\S+", "-XX:MetaspaceSize=256m", "PermGen replaced with Metaspace for JDK 8+"),
        ("-XX:MaxPermSize=\\S+", "-XX:MaxMetaspaceSize=512m", "MaxPermSize replaced with MaxMetaspaceSize"),
        ("-XX:\\+UseConcMarkSweepGC", "-XX:+UseG1GC", "CMS collector replaced with G1 for modern JDK"),
        ("-XX:\\+CMSPermGenSweepingEnabled", "", "Removed deprecated CMS PermGen flag"),
    ];

    public static ConfigTransformationRecord? Transform(string workspace, MigrationConfiguration config)
    {
        var domainHome = config.Source.DomainHome;
        if (string.IsNullOrWhiteSpace(domainHome)) return null;

        var script = Path.Combine(domainHome, "bin", "setDomainEnv.cmd");
        if (!File.Exists(script))
            script = Path.Combine(domainHome, "bin", "setDomainEnv.sh");
        if (!File.Exists(script)) return null;

        var original = TransformationSafeIO.ReadSourceFileSafe(script);
        var transformed = original;
        var notes = new List<string>();

        foreach (var (pattern, replacement, note) in Replacements)
        {
            if (Regex.IsMatch(transformed, pattern, RegexOptions.IgnoreCase))
            {
                transformed = Regex.Replace(transformed, pattern, replacement, RegexOptions.IgnoreCase);
                notes.Add(note);
            }
        }

        if (notes.Count == 0 && config.DomainAnalysis.DeprecatedJvmFlags.Count > 0)
        {
            foreach (var flag in config.DomainAnalysis.DeprecatedJvmFlags)
                notes.Add($"Review deprecated flag: {flag}");
        }

        if (notes.Count == 0) return null;

        var relOut = $"{MigrationWorkspaceManager.ConfigsDir}/setDomainEnv-modernized.cmd";
        var record = new ConfigTransformationRecord
        {
            SourcePath         = script,
            OutputPath         = relOut,
            Summary            = "JVM startup script modernization for target JDK tier",
            OriginalExcerpt    = Truncate(original, 1200),
            TransformedExcerpt = Truncate(transformed, 1200),
            RemediationNotes   = notes,
        };

        return record;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\n... [truncated]";
}
