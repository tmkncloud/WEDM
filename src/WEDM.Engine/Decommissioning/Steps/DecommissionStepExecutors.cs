using System.ServiceProcess;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Workflow.Steps;

namespace WEDM.Engine.Decommissioning.Steps;

public sealed class DecommissionDiscoverStep : IDecommissionStepExecutor
{
    private readonly IEnvironmentDiscoveryService _discovery;

    public DecommissionDiscoverStep(IEnvironmentDiscoveryService discovery) => _discovery = discovery;

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var topo = await _discovery.DiscoverAsync(config, cancellationToken).ConfigureAwait(false);
        config.DiscoveredTopology = topo;
        return StepExecutionResult.Ok(
            $"Discovered {topo.MiddlewareHomes.Count} home(s), {topo.DomainHomes.Count} domain(s), " +
            $"{topo.WindowsServices.Count} service(s), {topo.Processes.Count} process(es).");
    }
}

public sealed class DecommissionValidateStep : IDecommissionStepExecutor
{
    private readonly IOracleHomeValidator _validator;

    public DecommissionValidateStep(IOracleHomeValidator validator) => _validator = validator;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var result = _validator.ValidateForRemoval(config, config.DiscoveredTopology);
        var summary = string.Join("; ", result.Checks.Take(8));
        return Task.FromResult(result.Passed
            ? StepExecutionResult.Ok($"Pre-removal validation passed. {summary}")
            : StepExecutionResult.Fail($"Pre-removal validation issues: {string.Join("; ", result.BlockingIssues)}"));
    }
}

public sealed class DecommissionGracefulShutdownStep : IDecommissionStepExecutor
{
    private readonly IOracleProcessManager _processes;
    private readonly ILoggingService _log;

    public DecommissionGracefulShutdownStep(IOracleProcessManager processes, ILoggingService log)
    {
        _processes = processes;
        _log       = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (config.Options.DryRun)
            return StepExecutionResult.Ok("Dry-run: would stop middleware processes.");

        var active = config.DiscoveredTopology?.Processes ?? _processes.DetectMiddlewareProcesses();
        if (active.Count == 0)
            return StepExecutionResult.Ok("No middleware processes detected.");

        var stop = await _processes.StopProcessesAsync(active, forceAfterTimeout: true, TimeSpan.FromSeconds(45), cancellationToken)
            .ConfigureAwait(false);

        foreach (var msg in stop.Messages)
            _log.Info(msg, "Decommission.Shutdown");

        return stop.FailedCount == 0
            ? StepExecutionResult.Ok($"Stopped {stop.StoppedCount} process(es).")
            : StepExecutionResult.Fail($"Failed to stop {stop.FailedCount} process(es).", retryRecommended: true);
    }
}

public sealed class DecommissionServiceCleanupStep : IDecommissionStepExecutor
{
    private readonly ILoggingService _log;

    public DecommissionServiceCleanupStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Options.RemoveWindowsServices)
            return Task.FromResult(StepExecutionResult.Ok("Windows service removal not selected — skipped."));

        if (config.Options.DryRun)
            return Task.FromResult(StepExecutionResult.Ok("Dry-run: would remove Oracle-related Windows services."));

        var removed = 0;
        var failed  = 0;
        var services = config.DiscoveredTopology?.WindowsServices ?? [];

        foreach (var svc in services.Where(s => s.IsOracleRelated))
        {
            try
            {
                using var sc = new ServiceController(svc.ServiceName);
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                }

                // sc.Delete() requires sc.exe on some platforms — use sc delete
                var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", $"delete \"{svc.ServiceName}\"")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit(30_000);
                if (p.ExitCode == 0) removed++;
                else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                _log.Warning($"Could not remove service {svc.ServiceName}: {ex.Message}", "Decommission.Services");
            }
        }

        return Task.FromResult(failed == 0
            ? StepExecutionResult.Ok($"Service cleanup complete ({removed} removed).")
            : StepExecutionResult.Ok($"Service cleanup partial: {removed} removed, {failed} require manual removal."));
    }
}

public sealed class DecommissionInventoryDetachStep : IDecommissionStepExecutor
{
    private readonly IOracleInventoryAnalyzer _inventory;

    public DecommissionInventoryDetachStep(IOracleInventoryAnalyzer inventory) => _inventory = inventory;

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Options.DetachInventoryHomes)
            return StepExecutionResult.Ok("Inventory detach not selected — skipped.");

        var analysis = _inventory.Analyze(config.Paths.OracleInventory, config.Paths.MiddlewareHome);
        var detachCount = 0;

        foreach (var home in analysis.Homes)
        {
            var result = await _inventory.DetachHomeAsync(
                home.Path,
                config.Paths.OracleInventory,
                config.Options.DryRun,
                cancellationToken).ConfigureAwait(false);
            if (result.Success) detachCount++;
        }

        config.LastReport ??= new DecommissionReport();
        config.LastReport.InventoryCleanup = analysis;

        return StepExecutionResult.Ok(
            config.Options.DryRun
                ? $"Dry-run: would detach {analysis.Homes.Count} inventory home(s)."
                : $"Inventory detach processed for {detachCount} home(s).");
    }
}

public sealed class DecommissionFilesystemStep : IDecommissionStepExecutor
{
    private readonly RemoveDomainStep _removeDomain;
    private readonly RemoveMiddlewareHomeStep _removeMw;
    private readonly RemoveOracleFoldersStep _removeFolders;
    private readonly RemoveJdkStep _removeJdk;
    private readonly IOracleCleanupService _cleanup;

    public DecommissionFilesystemStep(
        RemoveDomainStep removeDomain,
        RemoveMiddlewareHomeStep removeMw,
        RemoveOracleFoldersStep removeFolders,
        RemoveJdkStep removeJdk,
        IOracleCleanupService cleanup)
    {
        _removeDomain  = removeDomain;
        _removeMw      = removeMw;
        _removeFolders = removeFolders;
        _removeJdk     = removeJdk;
        _cleanup       = cleanup;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (config.Options.DryRun)
            return StepExecutionResult.Ok("Dry-run: would remove domains, middleware home, inventory folders, and JDK.");

        var deploy = DecommissionConfigurationMapper.ToDeployment(config);
        var outputs = new List<string>();

        var domain = await _removeDomain.ExecuteAsync(step, deploy, cancellationToken).ConfigureAwait(false);
        outputs.Add(domain.Output);

        var mw = await _removeMw.ExecuteAsync(step, deploy, cancellationToken).ConfigureAwait(false);
        outputs.Add(mw.Output);

        if (config.Options.AggressiveCleanup)
        {
            var jdk = await _removeJdk.ExecuteAsync(step, deploy, cancellationToken).ConfigureAwait(false);
            outputs.Add(jdk.Output);
        }

        var folders = await _removeFolders.ExecuteAsync(step, deploy, cancellationToken).ConfigureAwait(false);
        outputs.Add(folders.Output);

        var topo = config.DiscoveredTopology ?? new EnvironmentTopology();
        var mode = config.Options.AggressiveCleanup ? OracleCleanupMode.Aggressive : OracleCleanupMode.Safe;
        var clean = await _cleanup.CleanupAsync(config, topo, mode, dryRun: false, cancellationToken).ConfigureAwait(false);
        outputs.Add($"Cleanup removed {clean.RemovedPaths.Count} path(s).");

        return StepExecutionResult.Ok(string.Join(" | ", outputs.Where(o => !string.IsNullOrWhiteSpace(o))));
    }
}

public sealed class DecommissionRegistryStep : IDecommissionStepExecutor
{
    private readonly RemoveOracleRegistryKeysStep _registry;
    private readonly RemoveJavaEnvVarsStep _env;

    public DecommissionRegistryStep(RemoveOracleRegistryKeysStep registry, RemoveJavaEnvVarsStep env)
    {
        _registry = registry;
        _env      = env;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Options.CleanupRegistry && !config.Options.CleanupEnvironmentVariables)
            return StepExecutionResult.Ok("Registry / environment cleanup not selected — skipped.");

        if (config.Options.DryRun)
            return StepExecutionResult.Ok("Dry-run: would clean Oracle registry keys and environment variables.");

        var deploy = DecommissionConfigurationMapper.ToDeployment(config);
        var messages = new List<string>();

        if (config.Options.CleanupRegistry)
        {
            var reg = await _registry.ExecuteAsync(step, deploy, cancellationToken).ConfigureAwait(false);
            messages.Add(reg.Output);
        }

        if (config.Options.CleanupEnvironmentVariables)
        {
            var env = await _env.ExecuteAsync(step, deploy, cancellationToken).ConfigureAwait(false);
            messages.Add(env.Output);
        }

        return StepExecutionResult.Ok(string.Join(" | ", messages));
    }
}

public sealed class DecommissionPostValidateStep : IDecommissionStepExecutor
{
    private readonly IOracleProcessManager _processes;
    private readonly IOracleInventoryAnalyzer _inventory;

    public DecommissionPostValidateStep(IOracleProcessManager processes, IOracleInventoryAnalyzer inventory)
    {
        _processes = processes;
        _inventory = inventory;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();

        if (_processes.DetectMiddlewareProcesses().Count > 0)
            issues.Add("Middleware processes still running.");

        if (Directory.Exists(config.Paths.MiddlewareHome))
            issues.Add($"Middleware home still exists: {config.Paths.MiddlewareHome}");

        var analysis = _inventory.Analyze(config.Paths.OracleInventory, config.Paths.MiddlewareHome);
        if (analysis.Homes.Any(h => !h.IsStale && Directory.Exists(h.Path)))
            issues.Add("Inventory still references existing Oracle homes.");

        return Task.FromResult(issues.Count == 0
            ? StepExecutionResult.Ok("Post-decommission validation passed — environment clean.")
            : StepExecutionResult.Fail($"Post-validation issues: {string.Join("; ", issues)}", retryRecommended: false));
    }
}

public sealed class DecommissionReportStep : IDecommissionStepExecutor
{
    private readonly ILoggingService _log;

    public DecommissionReportStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var report = config.LastReport ?? new DecommissionReport
        {
            ConfigurationName = config.Name,
            DryRun              = config.Options.DryRun,
            Topology            = config.DiscoveredTopology,
        };

        report.CompletedAt = DateTimeOffset.UtcNow;
        report.FinalStatus = config.Options.DryRun
            ? DecommissionStatus.DryRunCompleted
            : report.StepsFailed > 0 ? DecommissionStatus.Partial : DecommissionStatus.Completed;

        config.LastReport = report;

        Directory.CreateDirectory(config.Paths.ReportsDirectory);
        var htmlPath = Path.Combine(config.Paths.ReportsDirectory, $"wedm-decommission-{report.ReportId:N}.html");
        var jsonPath = Path.Combine(config.Paths.ReportsDirectory, $"wedm-decommission-{report.ReportId:N}.json");

        File.WriteAllText(htmlPath, BuildHtmlReport(report, config));
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        _log.Info($"Decommission report written: {htmlPath}", "Decommission.Report");

        return Task.FromResult(StepExecutionResult.Ok($"Report: {htmlPath}"));
    }

    private static string BuildHtmlReport(DecommissionReport report, DecommissionConfiguration config)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<html><head><title>WEDM Decommission Report</title></head><body>");
        sb.AppendLine($"<h1>WEDM Decommission Report — {System.Net.WebUtility.HtmlEncode(config.Name)}</h1>");
        sb.AppendLine($"<p>Status: {report.FinalStatus} | Dry-run: {config.Options.DryRun}</p>");
        sb.AppendLine($"<p>Steps: {report.StepsSucceeded}/{report.TotalSteps} succeeded</p>");
        sb.AppendLine("<h2>Workflow Steps</h2><ul>");
        foreach (var s in report.Steps)
            sb.AppendLine($"<li>{System.Net.WebUtility.HtmlEncode(s.Name)} — {s.Status}</li>");
        sb.AppendLine("</ul></body></html>");
        return sb.ToString();
    }
}
