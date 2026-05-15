using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

public sealed class MigrationExecutionResult
{
    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; set; } = Guid.NewGuid();

    [JsonPropertyName("outcome")]
    public MigrationExecutionOutcome Outcome { get; set; } = MigrationExecutionOutcome.NotStarted;

    [JsonPropertyName("workspacePath")]
    public string WorkspacePath { get; set; } = string.Empty;

    [JsonPropertyName("stages")]
    public List<MigrationExecutionStageResult> Stages { get; set; } = [];

    [JsonPropertyName("checkpoints")]
    public List<ExecutionCheckpointRecord> Checkpoints { get; set; } = [];

    [JsonPropertyName("operatorApprovals")]
    public List<OperatorApprovalRecord> OperatorApprovals { get; set; } = [];

    [JsonPropertyName("wlstExecutions")]
    public List<WlstExecutionRecord> WlstExecutions { get; set; } = [];

    [JsonPropertyName("preflight")]
    public PreflightValidationResult Preflight { get; set; } = new();

    [JsonPropertyName("postValidation")]
    public ExecutionValidationSummary PostValidation { get; set; } = new();

    [JsonPropertyName("rollbackManifest")]
    public RollbackManifest RollbackManifest { get; set; } = new();

    [JsonPropertyName("executionLog")]
    public List<string> ExecutionLog { get; set; } = [];

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("totalDurationMs")]
    public long TotalDurationMs { get; set; }

    [JsonPropertyName("startedAtUtc")]
    public DateTimeOffset? StartedAtUtc { get; set; }

    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }

    [JsonPropertyName("reportPaths")]
    public List<string> ReportPaths { get; set; } = [];
}

public sealed class MigrationExecutionStageResult
{
    [JsonPropertyName("stage")]
    public MigrationExecutionStageKind Stage { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public MigrationExecutionStageStatus Status { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class ExecutionCheckpointRecord
{
    [JsonPropertyName("kind")]
    public ExecutionCheckpointKind Kind { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    [JsonPropertyName("decision")]
    public CheckpointDecisionKind? Decision { get; set; }

    [JsonPropertyName("decidedAtUtc")]
    public DateTimeOffset? DecidedAtUtc { get; set; }
}

public sealed class OperatorApprovalRecord
{
    [JsonPropertyName("checkpoint")]
    public ExecutionCheckpointKind Checkpoint { get; set; }

    [JsonPropertyName("decision")]
    public CheckpointDecisionKind Decision { get; set; }

    [JsonPropertyName("operatorNote")]
    public string? OperatorNote { get; set; }

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; set; }
}

public sealed class WlstExecutionRecord
{
    [JsonPropertyName("scriptName")]
    public string ScriptName { get; set; } = string.Empty;

    [JsonPropertyName("scriptPath")]
    public string ScriptPath { get; set; } = string.Empty;

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("stdoutExcerpt")]
    public string? StdoutExcerpt { get; set; }

    [JsonPropertyName("stderrExcerpt")]
    public string? StderrExcerpt { get; set; }

    [JsonPropertyName("logPath")]
    public string? LogPath { get; set; }
}

public sealed class PreflightValidationResult
{
    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("blockerCount")]
    public int BlockerCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("checks")]
    public List<PreflightCheckResult> Checks { get; set; } = [];
}

public sealed class PreflightCheckResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public PreflightSeverity Severity { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class ExecutionValidationSummary
{
    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("confidence")]
    public TransformationConfidenceKind Confidence { get; set; }

    [JsonPropertyName("messages")]
    public List<string> Messages { get; set; } = [];
}

public sealed class RollbackManifest
{
    [JsonPropertyName("checkpoints")]
    public List<string> Checkpoints { get; set; } = [];

    [JsonPropertyName("guidance")]
    public List<string> Guidance { get; set; } = [];

    [JsonPropertyName("executedScripts")]
    public List<string> ExecutedScripts { get; set; } = [];

    [JsonPropertyName("modifiedTargets")]
    public List<string> ModifiedTargets { get; set; } = [];
}

public sealed class MigrationExecutionOptions
{
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("selectedWlstScripts")]
    public List<string>? SelectedWlstScripts { get; set; }

    [JsonPropertyName("targetDomainHome")]
    public string? TargetDomainHome { get; set; }

    [JsonPropertyName("skipBackupCheckpoint")]
    public bool SkipBackupCheckpoint { get; set; }

    [JsonIgnore]
    public MigrationExecutionCredentials? Credentials { get; set; }

    [JsonPropertyName("operationTimeoutMinutes")]
    public int OperationTimeoutMinutes { get; set; } = 90;
}

/// <summary>In-memory credentials — never persisted to disk.</summary>
public sealed class MigrationExecutionCredentials
{
    public string WebLogicUsername { get; set; } = "weblogic";
    public string WebLogicPassword { get; set; } = string.Empty;
}

public sealed class CheckpointDecision
{
    public CheckpointDecisionKind Kind { get; set; }
    public string? OperatorNote { get; set; }
}
