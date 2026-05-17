namespace WEDM.Domain.Enums;

/// <summary>How WEDM applies Oracle partial-install remediation.</summary>
public enum AutoRemediationMode
{
    Disabled = 0,
    SuggestOnly = 1,
    ConfirmRequired = 2,
    AutomaticSafeOnly = 3,
    Aggressive = 4,
}

/// <summary>Rich classification for Oracle home / inventory remediation (distinct from install-time <see cref="Models.OracleHomeState"/>).</summary>
public enum OracleRemediationState
{
    Healthy = 0,
    PartialInstall = 1,
    Orphaned = 2,
    StaleInventoryRegistration = 3,
    InventoryOnly = 4,
    FilesystemOnly = 5,
    Locked = 6,
    ActiveInstall = 7,
    SafeToClean = 8,
    UnsafeToClean = 9,
    Unknown = 99,
}

public enum RemediationRiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

public enum RemediationConfidence
{
    Low = 0,
    Medium = 1,
    High = 2,
}

public enum RemediationActionType
{
    DeleteDirectory = 0,
    DeleteFile = 1,
    DeleteRetryTempDirectory = 2,
    DeleteExtractionFolder = 3,
    DeleteGeneratedResponseFile = 4,
    DeleteStaleLog = 5,
    DeleteStaleSnapshot = 6,
    DetachInventoryHome = 7,
    RebuildInventorySkeleton = 8,
    RemoveStaleLockFile = 9,
    StopProcess = 10,
    StopWindowsService = 11,
    CleanupJavaHome = 12,
    CleanupPathEntry = 13,
    CleanupTempEnvironmentVariable = 14,
    NoOp = 99,
}

public enum RemediationExecutionOutcome
{
    Skipped = 0,
    DryRun = 1,
    Succeeded = 2,
    Failed = 3,
    Partial = 4,
    UnsafeBlocked = 5,
}
