namespace WEDM.Domain.Enums;

/// <summary>
/// Supported Oracle WebLogic major versions.
/// </summary>
public enum WebLogicVersion
{
    Unknown   = 0,
    WLS_11g   = 11,   // 10.3.6 — JDK 7/8
    WLS_12c   = 12,   // 12.2.1.4 — JDK 8
    WLS_14c   = 14    // 14.1.2 — JDK 21
}

/// <summary>
/// Target operating system platform for deployment.
/// </summary>
public enum DeploymentPlatform
{
    WindowsServer = 1,
    Linux         = 2,
    Docker        = 3,    // Phase 3+
    OCI           = 4     // Phase 4+
}

/// <summary>
/// Deployment mode that controls how the engine executes.
/// </summary>
public enum DeploymentMode
{
    Interactive = 1,   // GUI wizard drives deployment
    Silent      = 2,   // Fully unattended via config file
    Validation  = 3,   // Dry run — validate only, no changes
    Rollback    = 4    // Restore from snapshot
}

/// <summary>
/// Lifecycle state of a single deployment session.
/// </summary>
public enum DeploymentStatus
{
    NotStarted   = 0,
    InProgress   = 1,
    Paused       = 2,
    Completed    = 3,
    Failed       = 4,
    RolledBack   = 5,
    PartialFail  = 6   // Some steps failed; partial state on disk
}

/// <summary>
/// Granular state of an individual workflow step.
/// </summary>
public enum StepStatus
{
    Pending    = 0,
    Running    = 1,
    Succeeded  = 2,
    Failed     = 3,
    Skipped    = 4,
    Retrying   = 5
}

/// <summary>
/// Severity level of a validation finding.
/// </summary>
public enum ValidationSeverity
{
    Info    = 0,
    Warning = 1,
    Error   = 2,
    Fatal   = 3
}

/// <summary>
/// Type of WebLogic server being configured.
/// </summary>
public enum ServerType
{
    AdminServer   = 1,
    ManagedServer = 2,
    NodeManager   = 3
}

/// <summary>
/// Domain topology type.
/// </summary>
public enum DomainTopology
{
    SingleServer  = 1,   // AdminServer only
    Standard      = 2,   // Admin + 1..N managed servers
    Clustered     = 3,   // Admin + cluster + managed servers
    HighAvailability = 4 // Phase 3+
}

/// <summary>
/// Components that can be selectively installed.
/// </summary>
[Flags]
public enum InstallationComponents
{
    None            = 0,
    JDK             = 1 << 0,
    VCRedist        = 1 << 1,
    WebLogicServer  = 1 << 2,
    Infrastructure  = 1 << 3,
    FormsReports    = 1 << 4,
    OHSWebTier      = 1 << 5,
    AllWindows      = JDK | VCRedist | WebLogicServer | Infrastructure | FormsReports | OHSWebTier
}

/// <summary>
/// Log level used internally by the WEDM logging service.
/// </summary>
public enum LogLevel
{
    Verbose  = 0,
    Debug    = 1,
    Info     = 2,
    Warning  = 3,
    Error    = 4,
    Fatal    = 5
}
