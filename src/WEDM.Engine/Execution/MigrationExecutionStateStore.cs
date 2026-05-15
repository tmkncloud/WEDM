using System.Text.Json;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Migration;

namespace WEDM.Engine.Execution;

public sealed class MigrationExecutionStateStore : IMigrationExecutionStateStore
{
    public const string ExecutionDir = "execution";
    public const string StateFile    = "execution-state.json";
    public const string HistoryFile  = "execution-history.json";

    public async Task SaveAsync(string workspacePath, MigrationExecutionResult result, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(workspacePath, ExecutionDir);
        Directory.CreateDirectory(dir);

        var statePath = Path.Combine(dir, StateFile);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(result, MigrationJsonOptions.Create()), cancellationToken);

        var historyPath = Path.Combine(dir, HistoryFile);
        var history = await LoadHistoryAsync(historyPath, cancellationToken);
        history.Add(new ExecutionHistoryEntry
        {
            SessionId   = result.SessionId,
            Outcome     = result.Outcome,
            StartedAtUtc = result.StartedAtUtc,
            CompletedAtUtc = result.CompletedAtUtc,
            DurationMs  = result.TotalDurationMs,
        });
        await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(history, MigrationJsonOptions.Create()), cancellationToken);
    }

    public async Task<MigrationExecutionResult?> LoadAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var statePath = Path.Combine(workspacePath, ExecutionDir, StateFile);
        if (!File.Exists(statePath)) return null;
        var json = await File.ReadAllTextAsync(statePath, cancellationToken);
        return JsonSerializer.Deserialize<MigrationExecutionResult>(json, MigrationJsonOptions.Create());
    }

    private static async Task<List<ExecutionHistoryEntry>> LoadHistoryAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return [];
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<List<ExecutionHistoryEntry>>(json, MigrationJsonOptions.Create()) ?? [];
    }

    private sealed class ExecutionHistoryEntry
    {
        public Guid SessionId { get; set; }
        public MigrationExecutionOutcome Outcome { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public long DurationMs { get; set; }
    }
}
