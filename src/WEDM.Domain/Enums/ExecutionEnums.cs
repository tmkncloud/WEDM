namespace WEDM.Domain.Enums;

public enum MigrationExecutionStageKind
{
    WorkspaceValidation,
    PreflightValidation,
    BackupValidation,
    RollbackCheckpoint,
    WlstDryRun,
    WlstExecution,
    DomainRecreationValidation,
    JdbcValidation,
    NodeManagerValidation,
    PostValidation,
    ExecutionReporting,
}

public enum MigrationExecutionStageStatus
{
    Pending,
    Running,
    AwaitingApproval,
    Completed,
    Skipped,
    Failed,
    Cancelled,
}

public enum ExecutionCheckpointKind
{
    ReviewWlstScripts,
    ConfirmBackupAvailable,
    ConfirmTargetReadiness,
    ConfirmCredentials,
    ConfirmWlstExecution,
    ConfirmTopologyRecreation,
    ConfirmCutoverReadiness,
}

public enum CheckpointDecisionKind
{
    Approve,
    Pause,
    Abort,
}

public enum MigrationExecutionOutcome
{
    NotStarted,
    InProgress,
    Paused,
    Completed,
    CompletedWithWarnings,
    Failed,
    Cancelled,
}

public enum PreflightSeverity
{
    Informational,
    Warning,
    Blocker,
}
