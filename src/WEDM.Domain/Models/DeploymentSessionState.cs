using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

/// <summary>
/// Durable deployment session persisted for crash recovery, resume, and operator diagnostics.
/// Written atomically after each workflow checkpoint.
/// </summary>
public sealed class DeploymentSessionState
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; set; }

    [JsonPropertyName("configurationId")]
    public Guid ConfigurationId { get; set; }

    [JsonPropertyName("lifecycleStatus")]
    public DeploymentLifecycleStatus LifecycleStatus { get; set; } = DeploymentLifecycleStatus.InProgress;

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("lastCheckpointAt")]
    public DateTimeOffset LastCheckpointAt { get; set; }

    [JsonPropertyName("lastHeartbeatAt")]
    public DateTimeOffset LastHeartbeatAt { get; set; }

    [JsonPropertyName("machineName")]
    public string MachineName { get; set; } = Environment.MachineName;

    [JsonPropertyName("executedBy")]
    public string ExecutedBy { get; set; } = Environment.UserName;

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("overallProgressPercent")]
    public double OverallProgressPercent { get; set; }

    [JsonPropertyName("currentStepName")]
    public string? CurrentStepName { get; set; }

    [JsonPropertyName("configuration")]
    public DeploymentConfiguration Configuration { get; set; } = new();

    [JsonPropertyName("steps")]
    public List<DeploymentStepSnapshot> Steps { get; set; } = [];

    [JsonPropertyName("validation")]
    public PrerequisiteValidationResult? Validation { get; set; }

    [JsonPropertyName("rollback")]
    public RollbackSummary? Rollback { get; set; }

    [JsonPropertyName("report")]
    public DeploymentReport? Report { get; set; }

    [JsonPropertyName("payloadState")]
    public PayloadSessionState? PayloadState { get; set; }

    [JsonPropertyName("attemptHistory")]
    public List<StepAttemptRecord> AttemptHistory { get; set; } = [];

    [JsonPropertyName("artifactPaths")]
    public List<string> ArtifactPaths { get; set; } = [];

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }

    [JsonPropertyName("lockToken")]
    public string? LockToken { get; set; }

    public bool CanResume =>
        LifecycleStatus is DeploymentLifecycleStatus.InProgress
            or DeploymentLifecycleStatus.Interrupted
            or DeploymentLifecycleStatus.Recoverable
            or DeploymentLifecycleStatus.Failed;

    public DeploymentStep? FindStep(string name)
        => Steps.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } snap
            ? snap.ToDeploymentStep()
            : null;
}

/// <summary>
/// JSON-serializable step snapshot for checkpoint restore.
/// Includes <see cref="RollbackCompensation"/> so that rollback after a process crash
/// uses the original captured Oracle paths rather than config-derived fallbacks.
/// Old checkpoint files that pre-date compensation serialisation will have
/// <see cref="RollbackCompensation"/> == null, which the rollback executors handle
/// gracefully by falling back to <see cref="DeploymentConfiguration"/> paths.
/// </summary>
public sealed class DeploymentStepSnapshot
{
    public Guid   Id             { get; set; }
    public int    Sequence       { get; set; }
    public string Name           { get; set; } = string.Empty;
    public string Description    { get; set; } = string.Empty;
    public string Category       { get; set; } = string.Empty;
    public StepStatus Status     { get; set; }
    public int    AttemptCount   { get; set; }
    public int    MaxRetries     { get; set; }
    public bool   IsRequired     { get; set; }
    public bool   CanRollback    { get; set; }
    public string? RollbackAction { get; set; }
    public string OutputLog      { get; set; } = string.Empty;
    public string ErrorMessage   { get; set; } = string.Empty;
    public int    ExitCode       { get; set; }
    public double ProgressPercent { get; set; }
    public DateTimeOffset? StartedAt   { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Persisted Oracle rollback compensation data captured when the step succeeded.
    /// Null for non-Oracle steps or when the step has not yet succeeded.
    /// When present and this snapshot is restored via <see cref="ToDeploymentStep"/>,
    /// the compensation's <see cref="OracleRollbackCompensation.Source"/> is changed from
    /// <see cref="CompensationSource.Runtime"/> to <see cref="CompensationSource.Restored"/>
    /// so diagnostics can distinguish crash-recovery from live execution.
    /// Old checkpoint files without this field deserialise with null — the rollback
    /// executors fall back to config-derived paths in that case (<see cref="CompensationSource.Fallback"/>).
    /// </summary>
    public OracleRollbackCompensation? RollbackCompensation { get; set; }

    public static DeploymentStepSnapshot FromStep(DeploymentStep step) => new()
    {
        Id                   = step.Id,
        Sequence             = step.Sequence,
        Name                 = step.Name,
        Description          = step.Description,
        Category             = step.Category,
        Status               = step.Status,
        AttemptCount         = step.AttemptCount,
        MaxRetries           = step.MaxRetries,
        IsRequired           = step.IsRequired,
        CanRollback          = step.CanRollback,
        RollbackAction       = step.RollbackAction,
        OutputLog            = step.OutputLog,
        ErrorMessage         = step.ErrorMessage,
        ExitCode             = step.ExitCode,
        ProgressPercent      = step.ProgressPercent,
        StartedAt            = step.StartedAt,
        CompletedAt          = step.CompletedAt,
        RollbackCompensation = step.RollbackCompensation,
    };

    public DeploymentStep ToDeploymentStep()
    {
        var step = new DeploymentStep
        {
            Id              = Id,
            Sequence        = Sequence,
            Name            = Name,
            Description     = Description,
            Category        = Category,
            Status          = Status,
            AttemptCount    = AttemptCount,
            MaxRetries      = MaxRetries,
            IsRequired      = IsRequired,
            CanRollback     = CanRollback,
            RollbackAction  = RollbackAction,
            OutputLog       = OutputLog,
            ErrorMessage    = ErrorMessage,
            ExitCode        = ExitCode,
            ProgressPercent = ProgressPercent,
            StartedAt       = StartedAt,
            CompletedAt     = CompletedAt,
            RollbackCompensation = RollbackCompensation,
        };

        // Tag compensation as Restored so diagnostics distinguish checkpoint-recovery
        // from live capture. Only Runtime → Restored; leave None and Fallback unchanged.
        if (step.RollbackCompensation is { Source: CompensationSource.Runtime })
            step.RollbackCompensation.Source = CompensationSource.Restored;

        return step;
    }
}

public sealed class StepAttemptRecord
{
    public string StepName { get; set; } = string.Empty;
    public int AttemptNumber { get; set; }
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PayloadSessionState
{
    public string? JdkInstallerPath { get; set; }
    public string? VcRedistInstallerPath { get; set; }
    public List<string> DownloadedArtifacts { get; set; } = [];
    public DateTimeOffset? LastValidatedAt { get; set; }
}
