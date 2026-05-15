using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation.Transformers;

internal static class NodeManagerConfigTransformer
{
    public static ConfigTransformationRecord? Transform(MigrationConfiguration config)
    {
        var path = config.DomainAnalysis.NodeManagerPropertiesPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        var original = TransformationSafeIO.ReadSourceFileSafe(path);
        var lines = original.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var notes = new List<string>();

        Upsert(lines, "SecureListener", "true", notes, "Enable secure Node Manager listener");
        Upsert(lines, "ListenPort", "5556", notes, "Standardize Node Manager port");
        Upsert(lines, "CrashRecoveryEnabled", "true", notes, "Enable crash recovery for production automation");

        var transformed = string.Join(Environment.NewLine, lines);
        return new ConfigTransformationRecord
        {
            SourcePath         = path,
            OutputPath         = $"{MigrationWorkspaceManager.ConfigsDir}/nodemanager-modernized.properties",
            Summary            = "Node Manager hardening preparation",
            OriginalExcerpt    = Truncate(original, 800),
            TransformedExcerpt = Truncate(transformed, 800),
            RemediationNotes   = notes,
        };
    }

    private static void Upsert(List<string> lines, string key, string value, List<string> notes, string note)
    {
        var idx = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            lines[idx] = $"{key}={value}";
        else
            lines.Add($"{key}={value}");
        notes.Add(note);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\n... [truncated]";
}
