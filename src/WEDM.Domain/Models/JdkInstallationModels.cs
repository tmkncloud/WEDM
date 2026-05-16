namespace WEDM.Domain.Models;

/// <summary>Captured diagnostics from a JDK install attempt (reporting + troubleshooting).</summary>
public sealed class JdkInstallationDiagnostics
{
    public string InstallerType        { get; set; } = string.Empty;
    public string InstallerPath        { get; set; } = string.Empty;
    public string TargetJavaHome       { get; set; } = string.Empty;
    public string ArgumentsDisplay     { get; set; } = string.Empty;
    public string WorkingDirectory     { get; set; } = string.Empty;
    public int    RawExitCode          { get; set; } = int.MinValue;
    public string NormalizedStatus     { get; set; } = string.Empty;
    public string NormalizedMessage    { get; set; } = string.Empty;
    public bool   Success              { get; set; }
    public bool   RebootRequired       { get; set; }
    public bool   SkippedAlreadyInstalled { get; set; }
    public string? PreExistingJavaHome  { get; set; }
    public string? ResolvedJavaHome    { get; set; }
    public string? JavaVersionOutput   { get; set; }
    public string? InstallerStdout     { get; set; }
    public string? InstallerStderr     { get; set; }
    public List<string> ValidationChecks { get; set; } = [];
}

public enum JdkInstallNormalizedStatus
{
    Unknown = 0,
    Success,
    SuccessRebootRequired,
    AlreadyInstalled,
    InvalidArguments,
    Failed,
    ValidationFailed,
    TimedOut
}
