using System.Text.Json;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Remediation;

public sealed class OraclePartialInstallRemediator : IOraclePartialInstallRemediator
{
    private readonly ILoggingService          _log;
    private readonly IOracleInventoryAnalyzer _inventoryAnalyzer;
    private readonly IOracleProcessManager    _processes;

    public OraclePartialInstallRemediator(
        ILoggingService log,
        IOracleInventoryAnalyzer inventoryAnalyzer,
        IOracleProcessManager processes)
    {
        _log               = log;
        _inventoryAnalyzer = inventoryAnalyzer;
        _processes         = processes;
    }

    public async Task<IReadOnlyList<RemediationActionResult>> ExecutePlanAsync(
        DeploymentConfiguration config,
        RemediationPlan plan,
        RemediationExecutionOptions options,
        RemediationCheckpoint? checkpoint,
        CancellationToken cancellationToken = default)
    {
        checkpoint ??= new RemediationCheckpoint
        {
            DeploymentId  = config.Id,
            AttemptNumber = config.CurrentInstallerContext?.AttemptNumber ?? 1,
        };

        var results = new List<RemediationActionResult>();

        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"{action.ActionType}:{action.TargetPath}";
            if (checkpoint.CompletedActionKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(new RemediationActionResult
                {
                    ActionType = action.ActionType,
                    TargetPath = action.TargetPath,
                    Outcome    = RemediationExecutionOutcome.Skipped,
                    Message    = "Already completed in prior remediation attempt (idempotent checkpoint).",
                });
                continue;
            }

            var result = await ExecuteActionAsync(config, action, options, cancellationToken);
            results.Add(result);

            if (result.Outcome is RemediationExecutionOutcome.Succeeded or RemediationExecutionOutcome.DryRun)
            {
                checkpoint.CompletedActionKeys.Add(key);
                PersistCheckpoint(config, checkpoint);
            }
        }

        return results;
    }

    private async Task<RemediationActionResult> ExecuteActionAsync(
        DeploymentConfiguration config,
        RemediationAction action,
        RemediationExecutionOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (options.DryRun)
            {
                _log.Info($"[Remediation:dry-run] Would execute {action.ActionType} on '{action.TargetPath}'", "Remediation");
                return new RemediationActionResult
                {
                    ActionType = action.ActionType,
                    TargetPath = action.TargetPath,
                    Outcome    = RemediationExecutionOutcome.DryRun,
                    Message    = action.Description,
                };
            }

            switch (action.ActionType)
            {
                case RemediationActionType.DeleteDirectory:
                case RemediationActionType.DeleteExtractionFolder:
                case RemediationActionType.DeleteRetryTempDirectory:
                    if (Directory.Exists(action.TargetPath))
                    {
                        Directory.Delete(action.TargetPath, recursive: true);
                        _log.Info($"[Remediation] Deleted directory: {action.TargetPath}", "Remediation");
                    }
                    break;

                case RemediationActionType.DeleteFile:
                case RemediationActionType.DeleteGeneratedResponseFile:
                case RemediationActionType.DeleteStaleLog:
                case RemediationActionType.RemoveStaleLockFile:
                    if (File.Exists(action.TargetPath))
                    {
                        File.Delete(action.TargetPath);
                        _log.Info($"[Remediation] Deleted file: {action.TargetPath}", "Remediation");
                    }
                    break;

                case RemediationActionType.DetachInventoryHome:
                    var detach = await _inventoryAnalyzer.DetachHomeAsync(
                        action.TargetPath,
                        config.Paths.OracleInventory,
                        dryRun: false,
                        cancellationToken);
                    if (!detach.Success)
                    {
                        return new RemediationActionResult
                        {
                            ActionType = action.ActionType,
                            TargetPath = action.TargetPath,
                            Outcome    = RemediationExecutionOutcome.Failed,
                            Message    = detach.Message,
                        };
                    }
                    break;

                case RemediationActionType.StopProcess:
                    var procs = _processes.DetectMiddlewareProcesses();
                    foreach (var p in procs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var proc = System.Diagnostics.Process.GetProcessById(p.ProcessId);
                            proc.Kill(entireProcessTree: true);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning($"[Remediation] Could not stop PID {p.ProcessId}: {ex.Message}", "Remediation");
                        }
                    }
                    break;

                default:
                    return new RemediationActionResult
                    {
                        ActionType = action.ActionType,
                        TargetPath = action.TargetPath,
                        Outcome    = RemediationExecutionOutcome.Skipped,
                        Message    = $"Action type {action.ActionType} is not implemented in this release.",
                    };
            }

            return new RemediationActionResult
            {
                ActionType = action.ActionType,
                TargetPath = action.TargetPath,
                Outcome    = RemediationExecutionOutcome.Succeeded,
                Message    = action.Description,
            };
        }
        catch (Exception ex)
        {
            _log.Error($"[Remediation] Action {action.ActionType} failed for '{action.TargetPath}': {ex.Message}", category: "Remediation");
            return new RemediationActionResult
            {
                ActionType = action.ActionType,
                TargetPath = action.TargetPath,
                Outcome    = RemediationExecutionOutcome.Failed,
                Message    = ex.Message,
            };
        }
    }

    private void PersistCheckpoint(DeploymentConfiguration config, RemediationCheckpoint checkpoint)
    {
        try
        {
            var dir = config.Paths.ReportsDirectory;
            if (string.IsNullOrWhiteSpace(dir))
                return;

            Directory.CreateDirectory(dir);
            checkpoint.LastUpdated = DateTimeOffset.UtcNow;
            var path = Path.Combine(dir, $"remediation-checkpoint-{config.Id:N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.Verbose($"Could not persist remediation checkpoint: {ex.Message}", "Remediation");
        }
    }
}
