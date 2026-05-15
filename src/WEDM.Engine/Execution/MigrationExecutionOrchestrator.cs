using System.Diagnostics;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Transformation;
using WEDM.Engine.Wlst;

namespace WEDM.Engine.Execution;

public sealed class MigrationExecutionOrchestrator : IMigrationExecutionOrchestrator
{
    private readonly IMigrationPreflightValidator _preflight;
    private readonly IWlstExecutionService _wlst;
    private readonly IMigrationExecutionValidationEngine _validation;
    private readonly IMigrationExecutionStateStore _stateStore;
    private readonly IMigrationExecutionReportWriter _reportWriter;

    private MigrationExecutionSession? _session;

    public event EventHandler<MigrationExecutionProgressEventArgs>? ProgressChanged;
    public event EventHandler<MigrationExecutionCheckpointEventArgs>? CheckpointRequired;

    public MigrationExecutionSession? ActiveSession => _session;

    public MigrationExecutionOrchestrator(
        IMigrationPreflightValidator preflight,
        IWlstExecutionService wlst,
        IMigrationExecutionValidationEngine validation,
        IMigrationExecutionStateStore stateStore,
        IMigrationExecutionReportWriter reportWriter)
    {
        _preflight    = preflight;
        _wlst         = wlst;
        _validation   = validation;
        _stateStore   = stateStore;
        _reportWriter = reportWriter;
    }

    public void SubmitCheckpointDecision(CheckpointDecision decision)
        => _session?.SubmitCheckpoint(decision);

    public void CancelActiveExecution()
        => _session?.Cancel();

    public async Task<MigrationExecutionResult> ExecuteAsync(
        MigrationConfiguration configuration,
        MigrationExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_session is not null)
            throw new InvalidOperationException("An execution session is already active.");

        var session = new MigrationExecutionSession();
        _session = session;
        var result = session.Result;
        var sw = Stopwatch.StartNew();

        result.SessionId    = session.SessionId;
        result.WorkspacePath = configuration.TransformationWorkspacePath ?? string.Empty;
        result.DryRun       = options.DryRun;
        result.StartedAtUtc = DateTimeOffset.UtcNow;
        result.Outcome      = MigrationExecutionOutcome.InProgress;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);

        try
        {
            // 1. Workspace validation
            await RunStageAsync(session, MigrationExecutionStageKind.WorkspaceValidation, "Workspace validation",
                0.05, linked.Token, () =>
                {
                    if (string.IsNullOrWhiteSpace(result.WorkspacePath) || !Directory.Exists(result.WorkspacePath))
                        throw new InvalidOperationException("Migration workspace is missing.");
                    session.AppendLog("Workspace validated.");
                    return Task.CompletedTask;
                });

            // 2. Preflight
            await RunStageAsync(session, MigrationExecutionStageKind.PreflightValidation, "Pre-flight validation",
                0.12, linked.Token, () =>
                {
                    result.Preflight = _preflight.Validate(configuration, options);
                    session.AppendLog($"Preflight: {result.Preflight.BlockerCount} blockers, {result.Preflight.WarningCount} warnings.");
                    if (!result.Preflight.Passed)
                        throw new InvalidOperationException("Pre-flight validation failed — resolve blockers before execution.");
                    return Task.CompletedTask;
                });

            // 3. Backup checkpoint
            if (!options.SkipBackupCheckpoint)
            {
                var decision = await RequireCheckpointAsync(session, linked.Token,
                    ExecutionCheckpointKind.ConfirmBackupAvailable,
                    "Confirm backup availability",
                    "Verify that source domain, middleware homes, and configuration backups exist before proceeding.");
                if (decision.Kind == CheckpointDecisionKind.Abort) return Cancelled(session, result);
                if (decision.Kind == CheckpointDecisionKind.Pause) return Paused(session, result);
            }

            await RunStageAsync(session, MigrationExecutionStageKind.BackupValidation, "Backup validation",
                0.18, linked.Token, () =>
                {
                    session.AppendLog("Backup checkpoint acknowledged by operator.");
                    return Task.CompletedTask;
                });

            // 4. Rollback manifest
            var targetDomain = options.TargetDomainHome
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "WEDM", "target-domains", configuration.Topology.DomainName ?? "migration_domain");

            await RunStageAsync(session, MigrationExecutionStageKind.RollbackCheckpoint, "Rollback checkpoint preparation",
                0.22, linked.Token, async () =>
                {
                    result.RollbackManifest = RollbackCheckpointBuilder.Build(configuration, targetDomain);
                    var path = Path.Combine(result.WorkspacePath, MigrationExecutionStateStore.ExecutionDir, "rollback-manifest.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path,
                        System.Text.Json.JsonSerializer.Serialize(result.RollbackManifest, Infrastructure.Migration.MigrationJsonOptions.Create()),
                        linked.Token);
                    session.AppendLog("Rollback manifest written.");
                });

            // 5. Review WLST
            var reviewDecision = await RequireCheckpointAsync(session, linked.Token,
                ExecutionCheckpointKind.ReviewWlstScripts,
                "Review generated WLST scripts",
                "Confirm you have reviewed all scripts in the migration workspace wlst/ folder.");
            if (reviewDecision.Kind == CheckpointDecisionKind.Abort) return Cancelled(session, result);
            if (reviewDecision.Kind == CheckpointDecisionKind.Pause) return Paused(session, result);

            // 6. Credentials checkpoint (online scripts)
            if (options.Credentials is null || string.IsNullOrWhiteSpace(options.Credentials.WebLogicPassword))
            {
                var credDecision = await RequireCheckpointAsync(session, linked.Token,
                    ExecutionCheckpointKind.ConfirmCredentials,
                    "Confirm credentials",
                    "WebLogic credentials were not supplied. Online WLST scripts may fail unless credentials are provided.");
                if (credDecision.Kind == CheckpointDecisionKind.Abort) return Cancelled(session, result);
                if (credDecision.Kind == CheckpointDecisionKind.Pause) return Paused(session, result);
            }

            var wlstDir = Path.Combine(result.WorkspacePath, MigrationWorkspaceManager.WlstDir);
            var scripts = Directory.Exists(wlstDir)
                ? Directory.GetFiles(wlstDir, "*.py").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : [];

            if (options.SelectedWlstScripts?.Count > 0)
                scripts = scripts.Where(s => options.SelectedWlstScripts.Contains(Path.GetFileName(s), StringComparer.OrdinalIgnoreCase)).ToList();

            if (scripts.Count == 0)
                throw new InvalidOperationException("No WLST scripts found in workspace.");

            var mw = configuration.Target.MiddlewareHome ?? configuration.Source.MiddlewareHome!;
            var wlstCmd = WlstPathResolver.Resolve(mw);
            var wlstEnv = new WlstExecutionEnvironment
            {
                OracleHome = mw,
                JavaHome   = configuration.Target.JavaHome ?? configuration.Source.JavaHome,
            };
            var logDir  = Path.Combine(result.WorkspacePath, MigrationExecutionStateStore.ExecutionDir, "logs");
            Directory.CreateDirectory(logDir);

            // 7. WLST dry-run pass
            if (options.DryRun)
            {
                await RunStageAsync(session, MigrationExecutionStageKind.WlstDryRun, "WLST dry-run",
                    0.45, linked.Token, async () =>
                    {
                        foreach (var script in scripts)
                        {
                            var rec = await _wlst.ExecuteScriptAsync(wlstCmd, script, options.Credentials, dryRun: true, logDir, linked.Token, environment: wlstEnv);
                            result.WlstExecutions.Add(rec);
                            session.AppendLog($"[DRY-RUN] {rec.ScriptName}");
                        }
                    });
            }
            else
            {
                // 8. Confirm WLST execution
                var execDecision = await RequireCheckpointAsync(session, linked.Token,
                    ExecutionCheckpointKind.ConfirmWlstExecution,
                    "Confirm WLST execution",
                    $"Operator approval required to execute {scripts.Count} WLST script(s) against the target environment.");
                if (execDecision.Kind == CheckpointDecisionKind.Abort) return Cancelled(session, result);
                if (execDecision.Kind == CheckpointDecisionKind.Pause) return Paused(session, result);

                await RunStageAsync(session, MigrationExecutionStageKind.WlstExecution, "WLST execution",
                    0.70, linked.Token, async () =>
                    {
                        var timeout = TimeSpan.FromMinutes(Math.Max(5, options.OperationTimeoutMinutes));
                        foreach (var script in scripts)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            session.AppendLog($"Executing {Path.GetFileName(script)}…");
                            var rec = await _wlst.ExecuteScriptAsync(wlstCmd, script, options.Credentials, dryRun: false, logDir, linked.Token, timeout, wlstEnv);
                            result.WlstExecutions.Add(rec);
                            result.RollbackManifest.ExecutedScripts.Add(Path.GetFileName(script));
                            if (!rec.Success)
                                throw new InvalidOperationException($"WLST script failed: {rec.ScriptName} (exit {rec.ExitCode})");
                        }
                    });

                await RunStageAsync(session, MigrationExecutionStageKind.DomainRecreationValidation, "Domain recreation validation",
                    0.80, linked.Token, () =>
                    {
                        var stageVal = _validation.ValidateStage(configuration, MigrationExecutionStageKind.DomainRecreationValidation, result);
                        if (!stageVal.Passed)
                            session.AppendLog("Domain validation warnings: " + string.Join("; ", stageVal.Messages));
                        return Task.CompletedTask;
                    });
            }

            // 9. Post validation
            await RunStageAsync(session, MigrationExecutionStageKind.PostValidation, "Post-execution validation",
                0.88, linked.Token, () =>
                {
                    result.PostValidation = _validation.ValidatePostExecution(configuration, result);
                    return Task.CompletedTask;
                });

            // 10. Reports
            await RunStageAsync(session, MigrationExecutionStageKind.ExecutionReporting, "Execution reporting",
                0.95, linked.Token, async () =>
                {
                    var reportDir = Path.Combine(result.WorkspacePath, MigrationExecutionStateStore.ExecutionDir, "reports");
                    var json = await _reportWriter.WriteJsonAsync(configuration, result, reportDir, linked.Token);
                    var html = await _reportWriter.WriteHtmlAsync(configuration, result, reportDir, linked.Token);
                    result.ReportPaths.Add(json);
                    result.ReportPaths.Add(html);
                    session.AppendLog($"Reports: {Path.GetFileName(json)}, {Path.GetFileName(html)}");
                });

            result.Outcome = result.PostValidation.Passed
                ? (result.Preflight.WarningCount > 0 ? MigrationExecutionOutcome.CompletedWithWarnings : MigrationExecutionOutcome.Completed)
                : MigrationExecutionOutcome.CompletedWithWarnings;
        }
        catch (OperationCanceledException)
        {
            result.Outcome = MigrationExecutionOutcome.Cancelled;
            session.AppendLog("Execution cancelled.");
        }
        catch (Exception ex)
        {
            result.Outcome = MigrationExecutionOutcome.Failed;
            session.AppendLog($"Execution failed: {ex.Message}");
        }
        finally
        {
            sw.Stop();
            result.TotalDurationMs = sw.ElapsedMilliseconds;
            result.CompletedAtUtc  = DateTimeOffset.UtcNow;
            result.Stages        = session.Result.Stages;

            if (!string.IsNullOrWhiteSpace(result.WorkspacePath))
                await _stateStore.SaveAsync(result.WorkspacePath, result, CancellationToken.None);

            _session = null;
        }

        return result;
    }

    private async Task<CheckpointDecision> RequireCheckpointAsync(
        MigrationExecutionSession session,
        CancellationToken ct,
        ExecutionCheckpointKind kind,
        string title,
        string detail)
    {
        var checkpoint = new ExecutionCheckpointRecord
        {
            Kind     = kind,
            Title    = title,
            Detail   = detail,
            Required = true,
        };
        session.Result.Checkpoints.Add(checkpoint);
        CheckpointRequired?.Invoke(this, new MigrationExecutionCheckpointEventArgs { Checkpoint = checkpoint });

        var decision = await session.WaitForCheckpointAsync(checkpoint, ct);
        checkpoint.Decision    = decision.Kind;
        checkpoint.DecidedAtUtc = DateTimeOffset.UtcNow;
        session.Result.OperatorApprovals.Add(new OperatorApprovalRecord
        {
            Checkpoint   = kind,
            Decision     = decision.Kind,
            OperatorNote = decision.OperatorNote,
            TimestampUtc = DateTimeOffset.UtcNow,
        });
        session.AppendLog($"Checkpoint {kind}: {decision.Kind}");
        return decision;
    }

    private async Task RunStageAsync(
        MigrationExecutionSession session,
        MigrationExecutionStageKind kind,
        string displayName,
        double percent,
        CancellationToken ct,
        Func<Task> action)
    {
        var stage = new MigrationExecutionStageResult
        {
            Stage       = kind,
            DisplayName = displayName,
            Status      = MigrationExecutionStageStatus.Running,
        };
        session.Result.Stages.Add(stage);
        RaiseProgress(stage, percent);

        var sw = Stopwatch.StartNew();
        try
        {
            await action();
            stage.Status  = MigrationExecutionStageStatus.Completed;
            stage.Message = "Completed";
        }
        catch (Exception ex)
        {
            stage.Status  = MigrationExecutionStageStatus.Failed;
            stage.Message = ex.Message;
            throw;
        }
        finally
        {
            stage.DurationMs = sw.ElapsedMilliseconds;
            RaiseProgress(stage, percent, stage.Message);
        }
    }

    private static MigrationExecutionResult Cancelled(MigrationExecutionSession session, MigrationExecutionResult result)
    {
        result.Outcome = MigrationExecutionOutcome.Cancelled;
        session.AppendLog("Execution aborted by operator.");
        return result;
    }

    private static MigrationExecutionResult Paused(MigrationExecutionSession session, MigrationExecutionResult result)
    {
        result.Outcome = MigrationExecutionOutcome.Paused;
        session.AppendLog("Execution paused by operator.");
        return result;
    }

    private void RaiseProgress(MigrationExecutionStageResult stage, double percent, string? log = null)
    {
        ProgressChanged?.Invoke(this, new MigrationExecutionProgressEventArgs
        {
            Stage          = stage,
            OverallPercent = percent * 100,
            LogLine        = log,
        });
    }

}
