using System.Text;
using System.Text.Json;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Persistence;
using WEDM.Infrastructure.Security;

namespace WEDM.Infrastructure.Deployment;

/// <summary>Exports operator diagnostics bundle (timeline, retries, locks, environment snapshot).</summary>
public sealed class DeploymentDiagnosticsExporter
{
    public async Task<string> ExportBundleAsync(
        DeploymentSessionState state,
        IReadOnlyList<DeploymentLockDescriptor> locks,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var bundleDir = Path.Combine(outputDirectory, $"wedm-diagnostics-{state.SessionId:N}");
        Directory.CreateDirectory(bundleDir);

        var timeline = BuildTimeline(state);
        await File.WriteAllTextAsync(
            Path.Combine(bundleDir, "timeline.txt"),
            timeline,
            cancellationToken).ConfigureAwait(false);

        var metrics = new
        {
            state.SessionId,
            state.LifecycleStatus,
            state.OverallProgressPercent,
            CompletedSteps = state.Steps.Count(s => s.Status == Domain.Enums.StepStatus.Succeeded),
            FailedSteps    = state.Steps.Count(s => s.Status == Domain.Enums.StepStatus.Failed),
            RetryAttempts  = state.AttemptHistory.Count,
            RollbackSteps  = state.Rollback?.StepsRolledBack ?? 0,
            PayloadArtifacts = state.PayloadState?.DownloadedArtifacts?.Count ?? 0
        };
        await AtomicFileWriter.WriteAllTextAsync(
            Path.Combine(bundleDir, "metrics.json"),
            JsonSerializer.Serialize(metrics, DeploymentJsonOptions.Create()),
            cancellationToken).ConfigureAwait(false);

        await AtomicFileWriter.WriteAllTextAsync(
            Path.Combine(bundleDir, "environment.json"),
            DeploymentConfigurationSanitizer.ToSafeJson(state.Configuration),
            cancellationToken).ConfigureAwait(false);

        await AtomicFileWriter.WriteAllTextAsync(
            Path.Combine(bundleDir, "locks.json"),
            JsonSerializer.Serialize(locks, DeploymentJsonOptions.Create()),
            cancellationToken).ConfigureAwait(false);

        await AtomicFileWriter.WriteAllTextAsync(
            Path.Combine(bundleDir, "session.json"),
            JsonSerializer.Serialize(state, DeploymentJsonOptions.Create()),
            cancellationToken).ConfigureAwait(false);

        return bundleDir;
    }

    private static string BuildTimeline(DeploymentSessionState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Deployment: {state.Configuration.Name}");
        sb.AppendLine($"Session:    {state.SessionId:N}");
        sb.AppendLine($"Status:     {state.LifecycleStatus}");
        sb.AppendLine($"Started:    {state.StartedAt:u}");
        sb.AppendLine($"Checkpoint: {state.LastCheckpointAt:u}");
        sb.AppendLine();
        foreach (var step in state.Steps.OrderBy(s => s.Sequence))
        {
            sb.AppendLine($"[{step.Sequence:000}] {step.Name,-40} {step.Status,-12} " +
                          $"{step.StartedAt:HH:mm:ss} → {step.CompletedAt:HH:mm:ss}");
            if (!string.IsNullOrWhiteSpace(step.ErrorMessage))
                sb.AppendLine($"       ERROR: {SecretRedactor.Redact(step.ErrorMessage)}");
        }
        if (state.AttemptHistory.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("── Retry history ──");
            foreach (var a in state.AttemptHistory)
                sb.AppendLine($"{a.Timestamp:u}  {a.StepName}  attempt={a.AttemptNumber}  success={a.Success}  {a.Message}");
        }
        return sb.ToString();
    }
}
