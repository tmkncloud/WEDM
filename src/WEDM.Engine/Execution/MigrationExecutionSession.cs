using WEDM.Domain.Models;

namespace WEDM.Engine.Execution;

/// <summary>Per-run execution session supporting operator checkpoints and cancellation.</summary>
public sealed class MigrationExecutionSession
{
    private readonly object _lock = new();
    private TaskCompletionSource<CheckpointDecision>? _checkpointTcs;
    private CancellationTokenSource? _cts;

    public Guid SessionId { get; } = Guid.NewGuid();
    public MigrationExecutionResult Result { get; } = new();
    public bool IsCancelled { get; private set; }

    public CancellationToken Token
    {
        get
        {
            lock (_lock)
            {
                _cts ??= new CancellationTokenSource();
                return _cts.Token;
            }
        }
    }

    public async Task<CheckpointDecision> WaitForCheckpointAsync(
        ExecutionCheckpointRecord checkpoint,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _checkpointTcs = new TaskCompletionSource<CheckpointDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            using var reg = cancellationToken.Register(() => _checkpointTcs?.TrySetCanceled(cancellationToken));
            return await _checkpointTcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
                _checkpointTcs = null;
        }
    }

    public void SubmitCheckpoint(CheckpointDecision decision)
    {
        lock (_lock)
            _checkpointTcs?.TrySetResult(decision);
    }

    public void Cancel()
    {
        lock (_lock)
        {
            IsCancelled = true;
            _cts?.Cancel();
            _checkpointTcs?.TrySetResult(new CheckpointDecision { Kind = Domain.Enums.CheckpointDecisionKind.Abort });
        }
    }

    public void AppendLog(string line)
    {
        Result.ExecutionLog.Add($"{DateTimeOffset.UtcNow:u} | {line}");
    }
}
