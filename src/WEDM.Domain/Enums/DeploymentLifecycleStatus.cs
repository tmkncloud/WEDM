namespace WEDM.Domain.Enums;

/// <summary>Lifecycle of a persisted deployment session on disk.</summary>
public enum DeploymentLifecycleStatus
{
    NotStarted   = 0,
    InProgress   = 1,
    Paused       = 2,
    Completed    = 3,
    Failed       = 4,
    RolledBack   = 5,
    PartialFail  = 6,
    Interrupted  = 7,
    Recoverable  = 8
}
