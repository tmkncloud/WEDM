using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Application.Services;

/// <summary>
/// Operator-facing recovery API: list interrupted deployments, resume, discard, and inspect diagnostics.
/// </summary>
public sealed class DeploymentRecoveryManager
{
    private readonly IDeploymentSessionStore _sessions;
    private readonly IDeploymentLockService  _locks;
    private readonly ILoggingService         _log;

    public DeploymentRecoveryManager(
        IDeploymentSessionStore sessions,
        IDeploymentLockService locks,
        ILoggingService log)
    {
        _sessions = sessions;
        _locks    = locks;
        _log      = log;
    }

    public Task<IReadOnlyList<DeploymentSessionState>> ListRecoverableAsync(CancellationToken ct = default)
        => _sessions.ListRecoverableAsync(ct);

    public async Task<DeploymentSessionState?> GetSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            return await _sessions.LoadAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            _log.Error($"Corrupt deployment session {sessionId:N}: {ex.Message}", ex, "Recovery");
            return null;
        }
    }

    public async Task MarkInterruptedAsync(Guid sessionId, string? reason = null, CancellationToken ct = default)
    {
        var state = await _sessions.LoadAsync(sessionId, ct).ConfigureAwait(false);
        if (state is null) return;
        state.LifecycleStatus = DeploymentLifecycleStatus.Interrupted;
        state.FailureReason     = reason ?? "Application closed before deployment completed.";
        await _sessions.SaveAsync(state, ct).ConfigureAwait(false);
        await _locks.ReleaseAsync(sessionId, ct).ConfigureAwait(false);
    }

    public async Task DiscardSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _locks.ReleaseAsync(sessionId, ct).ConfigureAwait(false);
        await _sessions.DeleteAsync(sessionId, ct).ConfigureAwait(false);
        _log.Info($"Deployment session {sessionId:N} discarded.", "Recovery");
    }

    public async Task<DeploymentRecoveryDiagnostics> BuildDiagnosticsAsync(Guid sessionId, CancellationToken ct = default)
    {
        var state = await GetSessionAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {sessionId:N} not found.");

        var completed = state.Steps.Count(s => s.Status == StepStatus.Succeeded);
        var failed    = state.Steps.Count(s => s.Status == StepStatus.Failed);
        var pending   = state.Steps.Count(s => s.Status == StepStatus.Pending);

        return new DeploymentRecoveryDiagnostics
        {
            SessionId           = sessionId,
            LifecycleStatus     = state.LifecycleStatus,
            CanResume           = state.CanResume,
            CompletedStepCount  = completed,
            FailedStepCount     = failed,
            PendingStepCount    = pending,
            LastCheckpointAt    = state.LastCheckpointAt,
            FailureReason       = state.FailureReason,
            CurrentStepName     = state.CurrentStepName,
            OverallProgressPercent = state.OverallProgressPercent,
            AttemptHistory      = state.AttemptHistory,
            ActiveLocks         = await _locks.ListActiveLocksAsync(ct).ConfigureAwait(false)
        };
    }
}

public sealed class DeploymentRecoveryDiagnostics
{
    public Guid SessionId { get; init; }
    public DeploymentLifecycleStatus LifecycleStatus { get; init; }
    public bool CanResume { get; init; }
    public int CompletedStepCount { get; init; }
    public int FailedStepCount { get; init; }
    public int PendingStepCount { get; init; }
    public DateTimeOffset LastCheckpointAt { get; init; }
    public string? FailureReason { get; init; }
    public string? CurrentStepName { get; init; }
    public double OverallProgressPercent { get; init; }
    public IReadOnlyList<StepAttemptRecord> AttemptHistory { get; init; } = [];
    public IReadOnlyList<DeploymentLockDescriptor> ActiveLocks { get; init; } = [];
}
