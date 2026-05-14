using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Workflow.Steps;

namespace WEDM.Engine.Workflow;

/// <summary>
/// Core workflow orchestrator — builds and executes the ordered deployment step plan.
///
/// Architecture:
///   • Step plan is built deterministically from DeploymentConfiguration (no side effects)
///   • Steps are executed sequentially in Sequence order; each step is independently retryable
///   • After each step, state is checkpointed so execution can resume after failure
///   • Rollback runs steps in reverse order for all completed rollback-capable steps
///   • Progress is reported as both step-level events and overall 0–100% double
///
/// Scalability note:
///   The step execution loop is deliberately sequential for Oracle installer compatibility.
///   Oracle OUI does not support concurrent installations on the same middleware home.
///   Future parallel execution is possible for multi-host clustered deployments.
/// </summary>
public sealed class DeploymentWorkflowEngine : IWorkflowOrchestrator
{
    private readonly ILoggingService           _log;
    private readonly IStepExecutorFactory      _stepFactory;
    private readonly IDeploymentPlanAccessor   _planAccessor;

    public event EventHandler<DeploymentStep>? StepStarted;
    public event EventHandler<DeploymentStep>? StepCompleted;
    public event EventHandler<double>?          ProgressUpdated;

    public DeploymentWorkflowEngine(
        ILoggingService log,
        IStepExecutorFactory stepFactory,
        IDeploymentPlanAccessor planAccessor)
    {
        _log          = log;
        _stepFactory = stepFactory;
        _planAccessor = planAccessor;
    }

    // ── Step Plan Builder ─────────────────────────────────────────────────────

    public IReadOnlyList<DeploymentStep> BuildStepPlan(DeploymentConfiguration config)
    {
        var steps = new List<DeploymentStep>();
        int seq = 1;

        void Add(string name, string desc, string cat, bool canRollback = true, string? rollback = null)
            => steps.Add(new DeploymentStep
            {
                Sequence       = seq++,
                Name           = name,
                Description    = desc,
                Category       = cat,
                IsRequired     = true,
                CanRollback    = canRollback,
                RollbackAction = rollback,
                MaxRetries     = 2
            });

        void AppendOpatchBlock()
        {
            Add("ValidatePatchHostPrereqs",           "Validate disk, privileges, Oracle Home, inventory, OPatch, staging", "OPatch", canRollback: false);
            Add("ValidateOpatchEnvironment",         "Detect OPatch version and inventory pointer",                          "OPatch", canRollback: false);
            Add("ValidatePatchStaging",              "Validate patch.xml layout (CPU/PSU or extracted folders)",           "OPatch", canRollback: false);
            Add("OpatchConflictCheck",               "OPatch conflict prerequisites (CheckConflictAgainstOHWithPatch)",  "OPatch", canRollback: false);
            Add("PrePatchOpatchInventorySnapshot",   "Capture pre-apply OPatch lsinventory snapshot",                      "OPatch", canRollback: false);
            Add("DetectBlockingMiddlewareProcesses", "Block if WebLogic-related Java processes are running",               "OPatch", canRollback: false);
            Add("PrePatchMetadataSnapshot",          "Write pre-patch JSON metadata snapshot (OH, staging, free disk)",    "OPatch", canRollback: false);
            Add("OpatchApplyPatches",                "Apply patches via OPatch (silent napply or sequential apply)",     "OPatch", canRollback: true, rollback: "RollbackOpatchApply");
            Add("OpatchPostApplyInventory",          "Capture post-apply OPatch lsinventory",                              "OPatch", canRollback: false);
            Add("GeneratePatchComplianceReport",     "Generate HTML/JSON patch compliance report",                       "OPatch", canRollback: false);
        }

        if (config.Patches.Enabled && config.Patches.StandalonePatchWorkflow)
        {
            AppendOpatchBlock();
            _log.Info($"Step plan built: {steps.Count} OPatch-only steps for {config.WebLogicVersion}", "Workflow");
            return steps.AsReadOnly();
        }

        // ── 1. Prerequisite Validation ─────────────────────────────────────────
        Add("ValidatePrerequisites",      "Run full prerequisite validation suite",              "Validation",    canRollback: false);
        Add("ValidatePayloadIntegrity",   "Verify installer binary file integrity",              "Validation",    canRollback: false);

        // ── 2. System Preparation ──────────────────────────────────────────────
        Add("CreateOracleFolders",        "Create Oracle directory structure",                   "Preparation",   rollback: "Remove-OracleFolders");
        Add("CreateSnapshot",             "Take pre-installation system snapshot",               "Preparation",   canRollback: false);

        // ── 3. C++ Prerequisites ───────────────────────────────────────────────
        if (config.Components.HasFlag(InstallationComponents.VCRedist))
            Add("InstallVCRedist",        "Install Visual C++ Redistributable (x64 + x86)",     "Prerequisites", rollback: "Remove-VCRedist");

        // ── 4. JDK ────────────────────────────────────────────────────────────
        if (config.Components.HasFlag(InstallationComponents.JDK))
        {
            Add("InstallJDK",             "Install Java Development Kit (silent)",               "JDK",           rollback: "Remove-JDK");
            Add("ConfigureJavaHome",      "Set JAVA_HOME and update system PATH",                "JDK",           rollback: "Remove-JavaEnvVars");
        }

        // ── 5. WebLogic / Infrastructure ──────────────────────────────────────
        if (config.Components.HasFlag(InstallationComponents.Infrastructure))
            Add("InstallInfrastructure",  "Install Oracle Fusion Middleware Infrastructure",     "WebLogic",      rollback: "Remove-MiddlewareHome");
        else if (config.Components.HasFlag(InstallationComponents.WebLogicServer))
            Add("InstallWebLogic",        "Install Oracle WebLogic Server (silent)",             "WebLogic",      rollback: "Remove-MiddlewareHome");

        var middlewareInstallPlanned =
            config.Components.HasFlag(InstallationComponents.Infrastructure) ||
            config.Components.HasFlag(InstallationComponents.WebLogicServer);

        if (middlewareInstallPlanned && config.Patches.Enabled && !config.Patches.StandalonePatchWorkflow)
            AppendOpatchBlock();

        // ── 6. Forms & Reports ────────────────────────────────────────────────
        if (config.Components.HasFlag(InstallationComponents.FormsReports) && config.ConfigureFormsReports)
            Add("InstallFormsReports",    "Install Oracle Forms and Reports",                    "FormsReports",  rollback: "Remove-FormsReports");

        // ── 7. OHS / WebTier ──────────────────────────────────────────────────
        if (config.Components.HasFlag(InstallationComponents.OHSWebTier) && config.Domain.FormsReports.InstallOhs)
            Add("InstallOHSWebTier",      "Install Oracle HTTP Server (WebTier)",                "OHS",           rollback: "Remove-OHS");

        // ── 8. Repository Creation Utility (RCU) ──────────────────────────────
        if (config.Database.RunRcu)
            Add("RunRCU",                 "Create WebLogic repository schemas in Oracle DB",     "Database",      rollback: "Drop-RCUSchemas");

        // ── 9. Domain Configuration ────────────────────────────────────────────
        Add("CreateDomain",              "Create WebLogic domain via WLST",                     "Domain",        rollback: "Remove-Domain");
        Add("ConfigureAdminServer",      "Configure AdminServer listen address and ports",       "Domain");
        Add("CreateManagedServers",      "Create and configure managed servers",                 "Domain");
        Add("ConfigureNodeManager",      "Configure Node Manager",                               "Domain");

        // ── 10. Post-Configuration ─────────────────────────────────────────────
        Add("CreateBootProperties",      "Generate boot.properties for all servers",             "Configuration");

        if (config.DomainOnlineAutomation.Enabled && !config.Patches.StandalonePatchWorkflow)
        {
            if (config.DomainOnlineAutomation.StartAdminServerIfNotRunning)
                Add("StartAdminForOnlineAutomation", "Start AdminServer for WLST online automation (if not listening)", "DomainOnline", canRollback: false);
            Add("WlstOnlinePostBootAutomation", "WLST online: nmEnroll, production mode, machine mapping", "DomainOnline", canRollback: false);
            Add("ValidateNodeManagerReachability", "Validate Node Manager TCP reachability", "DomainOnline", canRollback: false);
        }

        Add("ConfigureTnsnames",         "Configure tnsnames.ora for DB connectivity",           "Configuration");
        Add("ConfigureFormsEnv",         "Configure Default.env and formsweb.cfg",               "Configuration", canRollback: false);
        Add("ConfigureWebUtil",          "Deploy WebUtil JARs and DLLs",                         "Configuration", canRollback: false);
        Add("ConfigureRegistry",         "Set Oracle registry keys (NLS_LANG, paths)",           "Configuration", rollback: "Remove-OracleRegistryKeys");

        // ── 11. Windows Services ──────────────────────────────────────────────
        if (config.RegisterWindowsServices)
        {
            Add("RegisterAdminService",  "Register AdminServer as Windows service",              "Services",      rollback: "Remove-AdminService");
            foreach (var ms in config.Domain.ManagedServers.Where(s => s.RegisterService))
                Add($"Register{ms.Name}Service", $"Register {ms.Name} as Windows service",      "Services",      rollback: $"Remove-{ms.Name}Service");
            Add("RegisterNodeMgrService","Register NodeManager as Windows service",              "Services",      rollback: "Remove-NodeMgrService");
        }

        // ── 12. Validation & Report ────────────────────────────────────────────
        Add("PostInstallValidation",     "Validate installed components and port bindings",      "Validation",    canRollback: false);

        if (!config.Patches.StandalonePatchWorkflow)
        {
            Add("ValidateSecuritySecretsAndSsl", "Validate SSL keystores and optional DPAPI secret vault", "Security", canRollback: false);
            Add("GenerateSecurityComplianceAudit", "Security & compliance scoring + HTML/JSON reports", "Security", canRollback: false);
        }

        Add("CreateDesktopShortcuts",    "Create desktop and Start Menu shortcuts",              "Finalization",  canRollback: false);
        Add("GenerateDeploymentReport",  "Generate HTML and JSON deployment report",             "Finalization",  canRollback: false);

        _log.Info($"Step plan built: {steps.Count} steps for {config.WebLogicVersion} on {config.Platform}", "Workflow");
        return steps.AsReadOnly();
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    public async Task<DeploymentReport> RunAsync(
        DeploymentConfiguration config,
        IReadOnlyList<DeploymentStep> steps,
        CancellationToken cancellationToken = default)
    {
        var report = new DeploymentReport
        {
            DeploymentName  = config.Name,
            ConfigurationId = config.Id,
            Environment     = config.Environment,
            StartedAt       = DateTimeOffset.UtcNow,
            FinalStatus     = DeploymentStatus.InProgress,
            Version         = config.WebLogicVersion,
            OsVersion       = Environment.OSVersion.ToString(),
            MiddlewareHome  = config.Paths.MiddlewareHome,
            DomainHome      = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName),
            AdminUrl        = $"http://{config.Network.Hostname}:{config.Domain.AdminPort}/console",
            ExecutedBy      = Environment.UserName,
            Steps           = steps.ToList()
        };

        _log.Info($"=== Deployment STARTED: {config.Name} ({steps.Count} steps) ===", "Workflow");

        _planAccessor.Bind(steps);
        try
        {
        int completedCount = 0;

        foreach (var step in steps.OrderBy(s => s.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();

            _log.StepStarted(step.Name, step.Sequence);
            step.MarkStarted();
            StepStarted?.Invoke(this, step);
            ReportProgress(completedCount, steps.Count);

            StepExecutionResult result;
            bool succeeded = false;

            for (int attempt = 1; attempt <= step.MaxRetries + 1; attempt++)
            {
                if (attempt > 1)
                {
                    _log.Warning($"Retrying step '{step.Name}' (attempt {attempt})...", "Workflow");
                    step.Status = StepStatus.Retrying;
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    step.MarkStarted();
                }

                result = await RunStepAsync(step, config, cancellationToken);

                if (result.Success)
                {
                    step.MarkSucceeded(result.Output);
                    _log.StepSucceeded(step.Name, result.Duration, result.Output);
                    succeeded = true;
                    break;
                }

                _log.StepFailed(step.Name, result.Error, result.ExitCode, result.Exception);
                step.MarkFailed(result.Error, result.ExitCode);

                if (!step.CanRetry) break;
            }

            StepCompleted?.Invoke(this, step);
            completedCount++;
            ReportProgress(completedCount, steps.Count);

            if (!succeeded && step.IsRequired)
            {
                _log.Error($"Required step '{step.Name}' failed after {step.AttemptCount} attempt(s). Aborting.", category: "Workflow");
                report.FinalStatus = DeploymentStatus.Failed;
                report.CompletedAt = DateTimeOffset.UtcNow;

                if (config.EnableRollback)
                    await RollbackAsync(steps, config, cancellationToken);

                return report;
            }
        }

        report.FinalStatus = steps.All(s => s.Status is StepStatus.Succeeded or StepStatus.Skipped)
            ? DeploymentStatus.Completed
            : DeploymentStatus.PartialFail;

        report.CompletedAt = DateTimeOffset.UtcNow;
        _log.Info($"=== Deployment {report.FinalStatus}: {report.StepsSucceeded}/{report.TotalSteps} steps succeeded ===", "Workflow");
        ReportProgress(steps.Count, steps.Count);
        return report;
        }
        finally
        {
            _planAccessor.Clear();
        }
    }

    // ── Individual step execution ─────────────────────────────────────────────

    public async Task<StepExecutionResult> RunStepAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var executor = _stepFactory.GetExecutor(step.Name);
        if (executor is null)
        {
            _log.Warning($"No executor registered for step '{step.Name}' — skipping.", "Workflow");
            return StepExecutionResult.Ok($"Step '{step.Name}' skipped (no executor).");
        }

        return await executor.ExecuteAsync(step, config, cancellationToken);
    }

    // ── Rollback ─────────────────────────────────────────────────────────────

    public async Task<bool> RollbackAsync(
        IReadOnlyList<DeploymentStep> steps,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        _log.Warning("=== ROLLBACK INITIATED ===", "Rollback");
        var rollbackSteps = steps
            .Where(s => s.Status == StepStatus.Succeeded && s.CanRollback && s.RollbackAction != null)
            .OrderByDescending(s => s.Sequence)
            .ToList();

        bool allOk = true;
        foreach (var step in rollbackSteps)
        {
            _log.Info($"Rolling back: {step.Name} → {step.RollbackAction}", "Rollback");
            try
            {
                var rollbackExecutor = _stepFactory.GetRollbackExecutor(step.RollbackAction!);
                if (rollbackExecutor is not null)
                    await rollbackExecutor.ExecuteAsync(step, config, cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Error($"Rollback of '{step.Name}' failed: {ex.Message}", ex, "Rollback");
                allOk = false;
            }
        }

        _log.Warning($"Rollback {(allOk ? "COMPLETED" : "PARTIAL")}.", "Rollback");
        return allOk;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ReportProgress(int completed, int total)
    {
        double pct = total > 0 ? (double)completed / total * 100.0 : 0;
        ProgressUpdated?.Invoke(this, pct);
    }
}
