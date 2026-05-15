using System.Text.Json;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Migration;

namespace WEDM.Engine.Transformation;

internal sealed class MigrationWorkspaceManager
{
    public const string DiscoveryDir     = "discovery";
    public const string PlansDir         = "plans";
    public const string WlstDir          = "wlst";
    public const string ConfigsDir       = "configs";
    public const string RemediationDir   = "remediation";
    public const string ValidationDir    = "validation";
    public const string ReportsDir       = "reports";
    public const string ManifestFile     = "manifest.json";
    public const string RollbackNotesFile = "rollback-notes.md";

    public string CreateWorkspace(MigrationConfiguration config, string? rootOverride = null)
    {
        var root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WEDM", "migration-workspaces");

        var folder = $"{Sanitize(config.Name)}-{config.Id:N}";
        var path   = Path.Combine(root, folder);
        Directory.CreateDirectory(path);

        foreach (var sub in new[] { DiscoveryDir, PlansDir, WlstDir, ConfigsDir, RemediationDir, ValidationDir, ReportsDir })
            Directory.CreateDirectory(Path.Combine(path, sub));

        return path;
    }

    public async Task WriteDiscoverySnapshotAsync(string workspace, MigrationConfiguration config, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(config, MigrationJsonOptions.Create());
        await TransformationSafeIO.WriteWorkspaceFileAsync(workspace, $"{DiscoveryDir}/discovery-snapshot.json", json, ct);
    }

    public async Task WriteManifestAsync(string workspace, TransformationWorkspaceManifest manifest, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(manifest, MigrationJsonOptions.Create());
        await TransformationSafeIO.WriteWorkspaceFileAsync(workspace, ManifestFile, json, ct);
    }

    public async Task WriteRollbackNotesAsync(string workspace, IEnumerable<string> notes, CancellationToken ct)
    {
        var content = "# Rollback preparation notes\n\n" + string.Join("\n", notes.Select(n => $"- {n}"));
        await TransformationSafeIO.WriteWorkspaceFileAsync(workspace, RollbackNotesFile, content, ct);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c)).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "migration" : safe[..Math.Min(safe.Length, 48)];
    }
}
