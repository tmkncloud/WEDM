using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Decommissioning;

public sealed class OracleCleanupService : IOracleCleanupService
{
    private readonly ILoggingService _log;

    public OracleCleanupService(ILoggingService log) => _log = log;

    public Task<OracleCleanupResult> CleanupAsync(
        DecommissionConfiguration config,
        EnvironmentTopology topology,
        OracleCleanupMode mode,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var result = new OracleCleanupResult { Mode = mode, Success = true };
        var targets = BuildCleanupTargets(config, topology, mode);

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (dryRun)
            {
                result.RemovedPaths.Add($"[dry-run] {target}");
                continue;
            }

            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                    result.RemovedPaths.Add(target);
                    _log.Info($"Removed file: {target}", "Decommission.Cleanup");
                }
                else if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                    result.RemovedPaths.Add(target);
                    _log.Info($"Removed directory: {target}", "Decommission.Cleanup");
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.SkippedPaths.Add(target);
                result.ManualFollowUps.Add($"{target}: {ex.Message}");
                _log.Warning($"Cleanup skipped {target}: {ex.Message}", "Decommission.Cleanup");
            }
        }

        return Task.FromResult(result);
    }

    private static IEnumerable<string> BuildCleanupTargets(
        DecommissionConfiguration config,
        EnvironmentTopology topology,
        OracleCleanupMode mode)
    {
        var paths = new List<string>();

        paths.AddRange(topology.TempExtractionFolders);

        if (mode == OracleCleanupMode.Aggressive)
        {
            paths.Add(config.Paths.TempDirectory);
            if (config.Options.RemoveSnapshots)
                paths.Add(config.Paths.SnapshotDirectory);
        }
        else
        {
            // Safe mode: only OUI extraction caches under temp
            if (Directory.Exists(config.Paths.TempDirectory))
            {
                paths.AddRange(Directory.GetDirectories(config.Paths.TempDirectory, "OraInstall*", SearchOption.TopDirectoryOnly));
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
