namespace WEDM.Domain.Enums;

/// <summary>Oracle environment cleanup aggressiveness.</summary>
public enum OracleCleanupMode
{
    Safe = 0,
    Aggressive = 1,
}

/// <summary>Decommission workflow phase identifiers.</summary>
public enum DecommissionPhase
{
    Discovery = 1,
    Validation = 2,
    GracefulShutdown = 3,
    ServiceCleanup = 4,
    InventoryDetach = 5,
    FilesystemCleanup = 6,
    RegistryCleanup = 7,
    PostValidation = 8,
    Reporting = 9,
}

/// <summary>Overall decommission session outcome.</summary>
public enum DecommissionStatus
{
    NotStarted,
    InProgress,
    Completed,
    Partial,
    Failed,
    DryRunCompleted,
}

/// <summary>Severity for conflict / orphan findings during deploy pre-flight.</summary>
public enum OracleConflictSeverity
{
    Informational,
    Warning,
    Error,
    Blocking,
}
