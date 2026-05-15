using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation.Transformers;

internal static class FormsConfigTransformer
{
    public static ConfigTransformationRecord? Transform(MigrationConfiguration config)
    {
        var formsHome = config.Source.FormsHome ?? config.FormsMetadata.ConfigurationPath;
        if (string.IsNullOrWhiteSpace(formsHome) || !Directory.Exists(formsHome)) return null;

        var cfg = Directory.EnumerateFiles(formsHome, "formsweb.cfg", SearchOption.AllDirectories).FirstOrDefault();
        if (cfg is null) return null;

        var original = TransformationSafeIO.ReadSourceFileSafe(cfg);
        var lines = original.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var notes = new List<string> { "Normalize Forms environment variables for target Fusion Middleware tier" };

        UpsertEnv(lines, "FORMS_JRE_HOME", "%JAVA_HOME%", notes);
        UpsertEnv(lines, "NLS_LANG", "AMERICAN_AMERICA.UTF8", notes);

        if (config.FormsMetadata.UsesWebUtil)
            notes.Add("WebUtil references detected — schedule client remediation before cutover");

        return new ConfigTransformationRecord
        {
            SourcePath         = cfg,
            OutputPath         = $"{MigrationWorkspaceManager.ConfigsDir}/formsweb-modernized.cfg",
            Summary            = "Forms configuration normalization",
            OriginalExcerpt    = Truncate(original, 1000),
            TransformedExcerpt = Truncate(string.Join(Environment.NewLine, lines), 1000),
            RemediationNotes   = notes,
        };
    }

    private static void UpsertEnv(List<string> lines, string key, string value, List<string> notes)
    {
        var prefix = key + "=";
        var idx = lines.FindIndex(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) lines[idx] = prefix + value;
        else lines.Add(prefix + value);
        notes.Add($"Set {key}");
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\n... [truncated]";
}
