using WEDM.Domain.Models;

namespace WEDM.Engine.Execution;

internal static class RollbackCheckpointBuilder
{
    public static RollbackManifest Build(MigrationConfiguration config, string targetDomainHome)
    {
        var manifest = new RollbackManifest
        {
            Checkpoints =
            [
                "Source middleware and domain homes preserved (read-only during WEDM execution)",
                $"Execution workspace: {config.TransformationWorkspacePath}",
                $"Target domain path: {targetDomainHome}",
            ],
            Guidance =
            [
                "Do not delete source environment until post-migration validation completes",
                "Retain WLST execution logs under workspace/execution/logs",
                "Document DNS and load balancer switchback before cutover",
                "If rollback required: stop target managed servers, restore traffic to source, validate source health",
            ],
        };

        manifest.ModifiedTargets.Add(targetDomainHome);
        if (!string.IsNullOrWhiteSpace(config.Source.DomainHome))
            manifest.Checkpoints.Add($"Source domain (unchanged): {config.Source.DomainHome}");

        return manifest;
    }
}
