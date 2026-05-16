using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Decommissioning.Steps;

namespace WEDM.Engine.Decommissioning;

public sealed class DecommissionWorkflowEngine : IDecommissionWorkflowEngine
{
    private readonly ILoggingService _log;
    private readonly IReadOnlyDictionary<string, IDecommissionStepExecutor> _executors;

    public DecommissionWorkflowEngine(
        ILoggingService log,
        DecommissionDiscoverStep discover,
        DecommissionValidateStep validate,
        DecommissionGracefulShutdownStep shutdown,
        DecommissionServiceCleanupStep services,
        DecommissionInventoryDetachStep inventory,
        DecommissionFilesystemStep filesystem,
        DecommissionRegistryStep registry,
        DecommissionPostValidateStep postValidate,
        DecommissionReportStep report)
    {
        _log = log;
        _executors = new Dictionary<string, IDecommissionStepExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["DecommissionDiscover"]        = discover,
            ["DecommissionValidate"]        = validate,
            ["DecommissionGracefulShutdown"]= shutdown,
            ["DecommissionServiceCleanup"]  = services,
            ["DecommissionInventoryDetach"] = inventory,
            ["DecommissionFilesystem"]      = filesystem,
            ["DecommissionRegistry"]        = registry,
            ["DecommissionPostValidate"]    = postValidate,
            ["DecommissionReport"]          = report,
        };
    }

    public IReadOnlyList<DeploymentStep> BuildStepPlan(DecommissionConfiguration config)
    {
        var steps = new List<DeploymentStep>();
        int seq = 1;

        void Add(string name, string desc, string category)
        {
            steps.Add(new DeploymentStep
            {
                Sequence    = seq++,
                Name        = name,
                Description = desc,
                Category    = category,
                MaxRetries  = name is "DecommissionGracefulShutdown" or "DecommissionFilesystem" ? 1 : 0,
                CanRollback = false,
            });
        }

        Add("DecommissionDiscover",        "Discover Oracle middleware assets",           "Discovery");
        Add("DecommissionValidate",        "Validate locks, permissions, and processes", "Validation");
        Add("DecommissionGracefulShutdown","Gracefully stop middleware processes",        "Shutdown");
        Add("DecommissionServiceCleanup","Remove Oracle Windows services",              "Services");
        Add("DecommissionInventoryDetach","Detach Oracle homes from oraInventory",      "Inventory");
        Add("DecommissionFilesystem",      "Remove domains, middleware home, and caches","Filesystem");
        Add("DecommissionRegistry",        "Clean Oracle registry and environment variables","Registry");
        Add("DecommissionPostValidate",    "Verify environment is clean",                 "Validation");
        Add("DecommissionReport",          "Generate WEDM decommission report",           "Reporting");

        return steps;
    }

    public async Task<DecommissionReport> RunAsync(
        DecommissionConfiguration config,
        IReadOnlyList<DeploymentStep> steps,
        CancellationToken cancellationToken = default)
    {
        var report = new DecommissionReport
        {
            ConfigurationName = config.Name,
            DryRun              = config.Options.DryRun,
            StartedAt           = DateTimeOffset.UtcNow,
            FinalStatus         = DecommissionStatus.InProgress,
            Topology            = config.DiscoveredTopology,
            Steps               = steps.ToList(),
        };

        config.LastReport = report;

        _log.Info($"=== DECOMMISSION STARTED: {config.Name} ({steps.Count} steps) ===", "Decommission");

        foreach (var step in steps.OrderBy(s => s.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_executors.TryGetValue(step.Name, out var executor))
            {
                step.MarkFailed($"No executor for step '{step.Name}'.", -1);
                report.FinalStatus = DecommissionStatus.Failed;
                break;
            }

            _log.StepStarted(step.Name, step.Sequence);
            step.MarkStarted();

            var result = await executor.ExecuteAsync(step, config, cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                step.MarkSucceeded(result.Output);
                _log.StepSucceeded(step.Name, result.Duration, result.Output);
            }
            else
            {
                step.MarkFailed(result.Error, result.ExitCode);
                _log.StepFailed(step.Name, result.Error, result.ExitCode, result.Exception, result.Notes);
                report.FailedRemovals.Add(new DecommissionAssetRecord
                {
                    AssetType = step.Category,
                    Path      = step.Name,
                    Outcome   = "Failed",
                    Notes     = result.Error,
                });

                if (step.IsRequired)
                {
                    report.FinalStatus = DecommissionStatus.Partial;
                    _log.Error($"Required decommission step '{step.Name}' failed. Aborting.", category: "Decommission");
                    break;
                }
            }
        }

        report.CompletedAt = DateTimeOffset.UtcNow;
        if (report.FinalStatus == DecommissionStatus.InProgress)
        {
            report.FinalStatus = report.StepsFailed > 0
                ? DecommissionStatus.Partial
                : (config.Options.DryRun ? DecommissionStatus.DryRunCompleted : DecommissionStatus.Completed);
        }

        // Generate report file via final step if not already run
        if (steps.All(s => s.Name != "DecommissionReport" || s.Status != StepStatus.Succeeded))
        {
            if (_executors.TryGetValue("DecommissionReport", out var reportExecutor))
            {
                var reportStep = steps.First(s => s.Name == "DecommissionReport");
                await reportExecutor.ExecuteAsync(reportStep, config, cancellationToken).ConfigureAwait(false);
            }
        }

        _log.Info($"=== DECOMMISSION {report.FinalStatus}: {report.StepsSucceeded}/{report.TotalSteps} steps succeeded ===", "Decommission");
        return report;
    }
}

/// <summary>Coordinates decommission workflow execution (application-facing facade).</summary>
public sealed class MiddlewareRemovalOrchestrator
{
    private readonly IDecommissionWorkflowEngine _workflow;
    private readonly ILoggingService _log;

    public MiddlewareRemovalOrchestrator(IDecommissionWorkflowEngine workflow, ILoggingService log)
    {
        _workflow = workflow;
        _log      = log;
    }

    public async Task<DecommissionReport> ExecuteAsync(
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        _log.Info($"Middleware removal orchestration started for {config.Name}", "Decommission");
        var steps  = _workflow.BuildStepPlan(config);
        var report = await _workflow.RunAsync(config, steps, cancellationToken).ConfigureAwait(false);
        return report;
    }
}
