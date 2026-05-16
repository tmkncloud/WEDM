using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.ResponseFiles;

namespace WEDM.Engine.Decommissioning;

/// <summary>
/// Isolates Oracle installer state between retries (unique temp dirs, response files, logs).
/// </summary>
public sealed class InstallRetryIsolationService : IInstallRetryIsolationService
{
    private readonly ILoggingService _log;
    private readonly ResponseFileGenerator _rspGen;
    private readonly IOracleCleanupService _cleanup;

    public InstallRetryIsolationService(
        ILoggingService log,
        ResponseFileGenerator rspGen,
        IOracleCleanupService cleanup)
    {
        _log     = log;
        _rspGen  = rspGen;
        _cleanup = cleanup;
    }

    public RetryIsolationContext PrepareRetryAttempt(
        DeploymentConfiguration config,
        string stepName,
        int attemptNumber)
    {
        if (!config.OracleLifecycle.IsolateRetries || attemptNumber <= 1)
        {
            return new RetryIsolationContext
            {
                IsolatedTempDirectory = config.Paths.TempDirectory,
                Actions               = ["Retry isolation disabled or first attempt."],
            };
        }

        var actions = new List<string>();
        var isolatedTemp = Path.Combine(
            config.Paths.TempDirectory,
            $"wedm-retry-{Sanitize(stepName)}-attempt{attemptNumber}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(isolatedTemp);
        config.Paths.TempDirectory = isolatedTemp;
        actions.Add($"Isolated temp directory: {isolatedTemp}");

        // Purge OUI extraction remnants from previous attempt under oracle temp root
        var parentTemp = Path.GetDirectoryName(isolatedTemp) ?? config.Paths.OracleRoot;
        PurgeOuiCaches(parentTemp, actions);

        string? rspPath = null;
        if (stepName.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase)
            || stepName.Contains("WebLogic", StringComparison.OrdinalIgnoreCase))
        {
            rspPath = _rspGen.GenerateWebLogicResponseFile(config);
            actions.Add($"Regenerated response file: {rspPath}");
        }

        if (config.OracleLifecycle.ForceCleanInstall)
        {
            var dc = new DecommissionConfiguration
            {
                Paths = new DecommissionPathConfiguration
                {
                    OracleRoot      = config.Paths.OracleRoot,
                    MiddlewareHome  = config.Paths.MiddlewareHome,
                    DomainBase      = config.Paths.DomainBase,
                    OracleInventory = config.Paths.OracleInventory,
                    TempDirectory   = parentTemp,
                },
            };
            var topo = new EnvironmentTopology();
            _ = _cleanup.CleanupAsync(dc, topo, Domain.Enums.OracleCleanupMode.Aggressive, dryRun: false);
            actions.Add("Force clean install: purged OUI extraction caches.");
        }

        _log.Warning(
            $"Retry isolation prepared for step '{stepName}' attempt {attemptNumber}: {isolatedTemp}",
            "Deploy.Retry");

        return new RetryIsolationContext
        {
            IsolatedTempDirectory   = isolatedTemp,
            RegeneratedResponseFile = rspPath,
            Actions                 = actions,
        };
    }

    private static void PurgeOuiCaches(string tempRoot, List<string> actions)
    {
        if (!Directory.Exists(tempRoot)) return;

        foreach (var dir in Directory.GetDirectories(tempRoot, "OraInstall*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                actions.Add($"Removed OUI cache: {dir}");
            }
            catch
            {
                actions.Add($"Could not remove OUI cache (locked): {dir}");
            }
        }
    }

    private static string Sanitize(string value)
        => new string(value.Where(char.IsLetterOrDigit).ToArray());
}
