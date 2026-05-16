using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Opatch;
using WEDM.Infrastructure.Security;

namespace WEDM.Application.Services;

/// <summary>
/// Top-level application service that coordinates validation, workflow execution,
/// and reporting. This is the primary entry point called by the UI layer.
///
/// Responsibilities:
///   • Sequence prerequisite validation → workflow execution → report generation
///   • Translate low-level engine events into UI-consumable progress events
///   • Manage deployment lifecycle (start, pause, resume, cancel, retry)
///   • Persist and restore checkpoint state across application restarts
/// </summary>
public sealed class DeploymentOrchestrator : IDisposable
{
    private readonly IWorkflowOrchestrator           _workflow;
    private readonly IValidationEngine               _validator;
    private readonly OpatchRunner                      _opatch;
    private readonly ILoggingService                   _log;
    private readonly IOperationalTelemetrySink         _telemetry;
    private readonly IDeploymentSessionStore         _sessions;
    private readonly IDeploymentLockService            _locks;
    private readonly DeploymentSecretLifecycleService  _secretLifecycle;
    private bool _disposed;

    // ── Public events (marshalled; subscribers need not dispatch) ─────────────

    /// <summary>Raised when an individual deployment step changes state or progress.</summary>
    public event EventHandler<WEDM.Domain.Interfaces.StepProgressEventArgs>? StepProgressChanged;

    /// <summary>Raised when overall deployment progress (0–100) changes.</summary>
    public event EventHandler<double>? OverallProgressChanged;

    public DeploymentOrchestrator(
        IWorkflowOrchestrator workflow,
        IValidationEngine     validator,
        OpatchRunner          opatch,
        ILoggingService       log,
        IOperationalTelemetrySink telemetry,
        IDeploymentSessionStore sessions,
        IDeploymentLockService locks,
        DeploymentSecretLifecycleService secretLifecycle)
    {
        _workflow        = workflow;
        _validator       = validator;
        _opatch          = opatch;
        _log             = log;
        _telemetry       = telemetry;
        _sessions        = sessions;
        _locks           = locks;
        _secretLifecycle = secretLifecycle;

        // Bridge engine events to orchestrator events
        _workflow.StepStarted    += OnStepStarted;
        _workflow.StepCompleted  += OnStepCompleted;
        _workflow.ProgressUpdated += OnProgressUpdated;
    }

    // ── Core Execution ────────────────────────────────────────────────────────

    /// <summary>Resume a previously interrupted deployment session.</summary>
    public async Task<DeploymentReport> ResumeDeploymentAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var state = await _sessions.LoadAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Deployment session {sessionId:N} not found.");

        if (!state.CanResume)
            throw new InvalidOperationException(
                $"Session {sessionId:N} is not resumable (status: {state.LifecycleStatus}).");

        _log.Info($"Resuming deployment session {sessionId:N} from checkpoint {state.LastCheckpointAt:u}.", "DeploymentOrchestrator");
        return await ExecuteDeploymentCoreAsync(state.Configuration, ct, resumeState: state).ConfigureAwait(false);
    }

    /// <summary>
    /// Execute a full deployment — validation, installation, configuration — and
    /// return a completed <see cref="DeploymentReport"/>.
    /// </summary>
    public async Task<DeploymentReport> ExecuteDeploymentAsync(
        DeploymentConfiguration config,
        CancellationToken        ct = default)
        => await ExecuteDeploymentCoreAsync(config, ct, resumeState: null).ConfigureAwait(false);

    private async Task<DeploymentReport> ExecuteDeploymentCoreAsync(
        DeploymentConfiguration config,
        CancellationToken ct,
        DeploymentSessionState? resumeState)
    {
        var sessionId = resumeState?.SessionId ?? Guid.NewGuid();
        var lockResult = await _locks.TryAcquireAsync(config, sessionId, ct).ConfigureAwait(false);
        if (!lockResult.Acquired)
        {
            _log.Error(lockResult.FailureReason ?? "Could not acquire deployment locks.", category: "DeploymentOrchestrator");
            return new DeploymentReport
            {
                ConfigurationId = config.Id,
                FinalStatus     = DeploymentStatus.Failed,
                MachineName     = Environment.MachineName,
                CompletedAt     = DateTimeOffset.UtcNow
            };
        }

        _log.BeginSession(config.Id, $"WebLogic {config.WebLogicVersion} deployment");
        _log.Info("Deployment started", "DeploymentOrchestrator", new
        {
            config.WebLogicVersion,
            config.Environment,
            config.Components,
            config.Paths.MiddlewareHome,
            config.Domain.DomainName
        });
        Telemetry("deployment.started", config, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["webLogicVersion"] = config.WebLogicVersion.ToString(),
            ["patchStandalone"] = config.Patches.StandalonePatchWorkflow.ToString()
        });

        try
        {
            // ── Phase 1: Prerequisites ──────────────────────────────────────
            PrerequisiteValidationResult prereqs;
            if (config.Patches.Enabled && config.Patches.StandalonePatchWorkflow)
            {
                RaiseStepSynthetic("Prerequisites", "Validating Oracle Home and OPatch patch readiness...", StepStatus.Running, 0);
                prereqs = await _validator.ValidateForPatchingAsync(config, ct);

                if (!prereqs.CanProceed)
                {
                    var fatalMsg = $"Patch readiness validation failed — {prereqs.Fatals} fatal error(s). Workflow aborted.";
                    _log.Error(fatalMsg, category: "DeploymentOrchestrator");
                    RaiseStepSynthetic("Prerequisites", fatalMsg, StepStatus.Failed, 100);

                    Telemetry("deployment.aborted_prerequisites", config, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["phase"] = "patch_prereqs"
                    });
                    return new DeploymentReport
                    {
                        ConfigurationId = config.Id,
                        FinalStatus     = DeploymentStatus.Failed,
                        Validation      = prereqs,
                        MachineName     = Environment.MachineName,
                        CompletedAt     = DateTimeOffset.UtcNow
                    };
                }

                RaiseStepSynthetic("Prerequisites", "Patch environment validation passed ✔", StepStatus.Succeeded, 100);
            }
            else
            {
                RaiseStepSynthetic("Prerequisites", "Validating system prerequisites...", StepStatus.Running, 0);
                prereqs = await RunPrerequisiteValidationAsync(config, ct);

                if (!prereqs.CanProceed)
                {
                    Engine.Validation.PrerequisiteValidationReporter.LogBlockingFindings(_log, prereqs, "DeploymentOrchestrator");
                    var fatalMsg = $"Prerequisite validation failed — {prereqs.Fatals} fatal error(s). Deployment aborted.";
                    _log.Error(fatalMsg, category: "DeploymentOrchestrator");
                    RaiseStepSynthetic("Prerequisites", fatalMsg, StepStatus.Failed, 100);

                    Telemetry("deployment.aborted_prerequisites", config, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["phase"] = "full_prereqs"
                    });
                    return new DeploymentReport
                    {
                        ConfigurationId = config.Id,
                        FinalStatus     = DeploymentStatus.Failed,
                        Validation      = prereqs,
                        MachineName     = Environment.MachineName,
                        CompletedAt     = DateTimeOffset.UtcNow
                    };
                }

                RaiseStepSynthetic("Prerequisites", "All prerequisites satisfied ✔", StepStatus.Succeeded, 100);
            }

            OverallProgressChanged?.Invoke(this, 5d);

            // ── Phase 2: Workflow Execution ─────────────────────────────────
            var steps = _workflow.BuildStepPlan(config);
            if (resumeState?.Steps.Count > 0)
                MergeResumeSteps(steps, resumeState);

            var runContext = DeploymentWorkflowRunContext.Fresh(
                sessionId,
                async (snapshot, token) =>
                {
                    snapshot.Validation = prereqs;
                    snapshot.LockToken  = sessionId.ToString("N");
                    await _sessions.SaveAsync(snapshot, token).ConfigureAwait(false);
                    await _locks.HeartbeatAsync(sessionId, token).ConfigureAwait(false);
                });

            if (resumeState is not null)
                runContext = new DeploymentWorkflowRunContext
                {
                    SessionId      = sessionId,
                    ResumeState    = resumeState,
                    CheckpointAsync = runContext.CheckpointAsync,
                    Heartbeat      = () => _ = _locks.HeartbeatAsync(sessionId)
                };

            await PersistSessionStartAsync(sessionId, config, steps, prereqs, ct).ConfigureAwait(false);

            var report = await _workflow.RunAsync(config, steps, runContext, ct).ConfigureAwait(false);
            report.Validation ??= prereqs;

            await FinalizeSessionAsync(sessionId, config, report, ct).ConfigureAwait(false);

            // ── Phase 3: Post-deployment report ────────────────────────────
            if (!string.IsNullOrWhiteSpace(config.Paths.ReportsDirectory))
            {
                Directory.CreateDirectory(config.Paths.ReportsDirectory);
                var htmlPath = Path.Combine(
                    config.Paths.ReportsDirectory,
                    $"wedm-report-{report.ReportId:N}.html");
                await _log.WriteHtmlReportAsync(report, htmlPath);
                _log.Info($"HTML report written: {htmlPath}", "DeploymentOrchestrator");
            }

            OverallProgressChanged?.Invoke(this, 100d);
            Telemetry("deployment.completed", config, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["finalStatus"] = report.FinalStatus.ToString()
            });
            return report;
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Deployment was cancelled by user.", "DeploymentOrchestrator");
            await MarkSessionInterruptedAsync(sessionId, "Cancelled by operator.", ct).ConfigureAwait(false);
            Telemetry("deployment.cancelled", config);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Unhandled exception: {ex.Message}", ex, "DeploymentOrchestrator");
            await MarkSessionInterruptedAsync(sessionId, ex.Message, ct).ConfigureAwait(false);
            Telemetry("deployment.failed", config, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["exception"] = ex.GetType().Name
            });
            throw;
        }
        finally
        {
            await _locks.ReleaseAsync(sessionId, ct).ConfigureAwait(false);
            _secretLifecycle.CleanupSession(config.Paths.TempDirectory);
            _log.EndSession();
        }
    }

    private async Task PersistSessionStartAsync(
        Guid sessionId,
        DeploymentConfiguration config,
        IReadOnlyList<DeploymentStep> steps,
        PrerequisiteValidationResult prereqs,
        CancellationToken ct)
    {
        var state = new DeploymentSessionState
        {
            SessionId       = sessionId,
            ConfigurationId = config.Id,
            LifecycleStatus = DeploymentLifecycleStatus.InProgress,
            StartedAt       = DateTimeOffset.UtcNow,
            LastCheckpointAt = DateTimeOffset.UtcNow,
            Configuration   = config,
            Steps           = steps.Select(DeploymentStepSnapshot.FromStep).ToList(),
            Validation      = prereqs
        };
        await _sessions.SaveAsync(state, ct).ConfigureAwait(false);
    }

    private async Task FinalizeSessionAsync(
        Guid sessionId,
        DeploymentConfiguration config,
        DeploymentReport report,
        CancellationToken ct)
    {
        var state = await _sessions.LoadAsync(sessionId, ct).ConfigureAwait(false);
        if (state is null) return;
        state.LifecycleStatus = report.FinalStatus switch
        {
            DeploymentStatus.Completed  => DeploymentLifecycleStatus.Completed,
            DeploymentStatus.RolledBack => DeploymentLifecycleStatus.RolledBack,
            DeploymentStatus.Failed     => DeploymentLifecycleStatus.Failed,
            _                           => DeploymentLifecycleStatus.PartialFail
        };
        state.Report = report;
        state.OverallProgressPercent = 100;
        await _sessions.SaveAsync(state, ct).ConfigureAwait(false);
    }

    private async Task MarkSessionInterruptedAsync(Guid sessionId, string reason, CancellationToken ct)
    {
        try
        {
            var state = await _sessions.LoadAsync(sessionId, ct).ConfigureAwait(false);
            if (state is null) return;
            state.LifecycleStatus = DeploymentLifecycleStatus.Interrupted;
            state.FailureReason   = reason;
            await _sessions.SaveAsync(state, ct).ConfigureAwait(false);
        }
        catch { /* best effort */ }
    }

    private static void MergeResumeSteps(IReadOnlyList<DeploymentStep> steps, DeploymentSessionState resume)
    {
        var byName = resume.Steps.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (!byName.TryGetValue(step.Name, out var snap)) continue;
            var restored = snap.ToDeploymentStep();
            step.Status       = restored.Status;
            step.AttemptCount = restored.AttemptCount;
            step.OutputLog    = restored.OutputLog;
            step.ErrorMessage = restored.ErrorMessage;
        }
    }

    /// <summary>
    /// Run prerequisite validation only (used by the Prerequisites wizard step).
    /// </summary>
    public async Task<PrerequisiteValidationResult> ValidatePrerequisitesAsync(
        DeploymentConfiguration config,
        CancellationToken ct = default)
    {
        return await RunPrerequisiteValidationAsync(config, ct);
    }

    /// <summary>
    /// Patch-oriented validation plus a live <c>opatch version</c> probe (used by Patch Management UI).
    /// </summary>
    public async Task<PatchReadinessResult> ValidatePatchReadinessAsync(
        DeploymentConfiguration config,
        CancellationToken ct = default)
    {
        var v = await _validator.ValidateForPatchingAsync(config, ct);
        if (!v.CanProceed)
            return new PatchReadinessResult { Validation = v };

        var ver = await _opatch.VersionAsync(config, ct);
        return new PatchReadinessResult
        {
            Validation            = v,
            OpatchVersionOutput   = ver.Output,
            OpatchVersionExitCode = ver.ExitCode
        };
    }

    /// <summary>
    /// Execute OPatch-only workflow (existing Oracle Home). Temporarily forces patch flags for plan building.
    /// </summary>
    public async Task<DeploymentReport> ExecutePatchWorkflowAsync(
        DeploymentConfiguration config,
        CancellationToken ct = default)
    {
        var savedEnabled   = config.Patches.Enabled;
        var savedStandalone = config.Patches.StandalonePatchWorkflow;
        config.Patches.Enabled               = true;
        config.Patches.StandalonePatchWorkflow = true;

        _log.BeginSession(config.Id, "OPatch patch-only workflow");
        _log.Info("Patch-only workflow started", "DeploymentOrchestrator", new
        {
            config.Paths.MiddlewareHome,
            config.Patches.PatchStagingDirectory
        });
        Telemetry("patch.workflow.started", config);

        try
        {
            RaiseStepSynthetic("PatchPrerequisites", "Validating Oracle Home and OPatch readiness...", StepStatus.Running, 0);
            var readiness = await ValidatePatchReadinessAsync(config, ct);
            if (!readiness.Validation.CanProceed || readiness.OpatchVersionExitCode != 0)
            {
                var msg = !readiness.Validation.CanProceed
                    ? "Patch readiness validation failed."
                    : $"OPatch version check failed (exit {readiness.OpatchVersionExitCode}).";
                _log.Error(msg, category: "DeploymentOrchestrator");
                RaiseStepSynthetic("PatchPrerequisites", msg, StepStatus.Failed, 100);

                Telemetry("patch.workflow.aborted_readiness", config);
                return new DeploymentReport
                {
                    ConfigurationId = config.Id,
                    FinalStatus     = DeploymentStatus.Failed,
                    Validation      = readiness.Validation,
                    MachineName     = Environment.MachineName,
                    CompletedAt     = DateTimeOffset.UtcNow
                };
            }

            RaiseStepSynthetic("PatchPrerequisites", "Patch environment ready ✔", StepStatus.Succeeded, 100);
            OverallProgressChanged?.Invoke(this, 5d);

            var steps  = _workflow.BuildStepPlan(config);
            var report = await _workflow.RunAsync(config, steps, ct);
            report.Validation = readiness.Validation;

            if (!string.IsNullOrWhiteSpace(config.Paths.ReportsDirectory))
            {
                Directory.CreateDirectory(config.Paths.ReportsDirectory);
                var htmlPath = Path.Combine(
                    config.Paths.ReportsDirectory,
                    $"wedm-report-{report.ReportId:N}.html");
                await _log.WriteHtmlReportAsync(report, htmlPath);
                _log.Info($"HTML deployment report written: {htmlPath}", "DeploymentOrchestrator");
            }

            OverallProgressChanged?.Invoke(this, 100d);
            Telemetry("patch.workflow.completed", config, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["finalStatus"] = report.FinalStatus.ToString()
            });
            return report;
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Patch workflow was cancelled by user.", "DeploymentOrchestrator");
            Telemetry("patch.workflow.cancelled", config);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Unhandled exception: {ex.Message}", ex, "DeploymentOrchestrator");
            Telemetry("patch.workflow.failed", config, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["exception"] = ex.GetType().Name
            });
            throw;
        }
        finally
        {
            config.Patches.Enabled               = savedEnabled;
            config.Patches.StandalonePatchWorkflow = savedStandalone;
            _log.EndSession();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<PrerequisiteValidationResult> RunPrerequisiteValidationAsync(
        DeploymentConfiguration config,
        CancellationToken ct)
    {
        var result = PrerequisiteValidationResult.New(config.Id);

        // Run independent checks in parallel
        var privTask  = _validator.ValidatePrivilegesAsync(ct);
        var osTask    = _validator.ValidateOperatingSystemAsync(ct);
        var hwTask    = _validator.ValidateHardwareAsync(config, ct);
        var diskTask  = _validator.ValidateDiskSpaceAsync(config, ct);
        var portsTask = _validator.ValidatePortsAsync(config, ct);
        var jdkTask   = _validator.ValidateJavaAsync(config, ct);
        var vcTask    = _validator.ValidateVcRedistAsync(ct);

        await Task.WhenAll(privTask, osTask, hwTask, diskTask, portsTask, jdkTask, vcTask);

        result.Merge(await privTask);
        result.Merge(await osTask);
        result.Merge(await hwTask);
        result.Merge(await diskTask);
        result.Merge(await portsTask);
        result.Merge(await jdkTask);
        result.Merge(await vcTask);

        if (config.Database.RunRcu)
        {
            var dbResult = await _validator.ValidateDatabaseConnectivityAsync(config, ct);
            result.Merge(dbResult);
        }

        var payloadResult = await _validator.ValidatePayloadIntegrityAsync(config, ct);
        result.Merge(payloadResult);

        if (!result.CanProceed)
            Engine.Validation.PrerequisiteValidationReporter.LogBlockingFindings(_log, result, "DeploymentOrchestrator");

        _log.Info(
            $"Prerequisites: {result.PassCount} passed, {result.WarnCount} warnings, " +
            $"{result.ErrorCount} errors, {result.Fatals} fatal - CanProceed={result.CanProceed}",
            "DeploymentOrchestrator");

        return result;
    }

    private static int[] GetRequiredPorts(DeploymentConfiguration config)
    {
        var ports = new List<int>();
        if (config.Domain.AdminPort               > 0) ports.Add(config.Domain.AdminPort);
        if (config.Domain.NodeManager.Port        > 0) ports.Add(config.Domain.NodeManager.Port);
        if (config.Domain.FormsReports.OhsPort    > 0) ports.Add(config.Domain.FormsReports.OhsPort);
        foreach (var ms in config.Domain.ManagedServers)
            if (ms.Port > 0) ports.Add(ms.Port);
        return [.. ports.Distinct()];
    }

    private void Telemetry(string eventName, DeploymentConfiguration config, IReadOnlyDictionary<string, string>? extra = null)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["configurationId"] = config.Id.ToString("N"),
            ["deploymentEnvironment"] = config.DeploymentEnvironment.ToString()
        };
        if (extra is not null)
        {
            foreach (var kv in extra)
                props[kv.Key] = kv.Value;
        }

        _telemetry.RecordEvent(eventName, props);
    }

    private void RaiseStepSynthetic(string name, string description, StepStatus status, double pct)
    {
        var step = new DeploymentStep
        {
            Name            = name,
            Description     = description,
            Status          = status,
            ProgressPercent = pct
        };
        if (status == StepStatus.Running)   step.MarkStarted();
        if (status == StepStatus.Succeeded) step.MarkSucceeded();
        if (status == StepStatus.Failed)    step.MarkFailed("Prerequisite check failed");

        StepProgressChanged?.Invoke(this, new WEDM.Domain.Interfaces.StepProgressEventArgs(step));
    }

    private void OnStepStarted(object? sender, DeploymentStep step)
        => StepProgressChanged?.Invoke(this, new WEDM.Domain.Interfaces.StepProgressEventArgs(step));

    private void OnStepCompleted(object? sender, DeploymentStep step)
        => StepProgressChanged?.Invoke(this, new WEDM.Domain.Interfaces.StepProgressEventArgs(step));

    private void OnProgressUpdated(object? sender, double pct)
    {
        // Scale workflow progress to 5–98% (prereqs = 0–5, report writing = 98–100)
        var scaled = 5d + (pct * 0.93);
        OverallProgressChanged?.Invoke(this, scaled);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workflow.StepStarted    -= OnStepStarted;
        _workflow.StepCompleted  -= OnStepCompleted;
        _workflow.ProgressUpdated -= OnProgressUpdated;
    }
}
