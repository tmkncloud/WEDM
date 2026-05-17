using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

// ── Failure classification ────────────────────────────────────────────────────

/// <summary>
/// Classifies the root cause of an OUI installer failure so that the retry
/// engine can apply the correct remediation before the next attempt.
/// </summary>
public enum InstallerFailureClass
{
    /// <summary>Failure cause is undetermined (default).</summary>
    Unknown = 0,

    /// <summary>
    /// Oracle Central Inventory already has a registration for the target home
    /// (INST-07319 / "Oracle Home already exists").
    /// Remediation: remove the stale inventory entry and delete the partial directory.
    /// </summary>
    InventoryConflict = 1,

    /// <summary>
    /// Active inventory lock file prevents OUI from starting.
    /// Remediation: wait for the lock owner to finish or remove a stale lock file.
    /// </summary>
    LockConflict = 2,

    /// <summary>
    /// Installer did not complete within the configured timeout window.
    /// Remediation: increase OuiInstallTimeoutMinutes or investigate hanging OUI process.
    /// </summary>
    Timeout = 3,

    /// <summary>
    /// Prerequisite check failed (insufficient disk space, missing VC runtime, etc.).
    /// Remediation: satisfy the blocking prerequisite before retrying.
    /// </summary>
    PrerequisiteFailure = 4,

    /// <summary>
    /// OUI could not parse or locate the response file / silent XML.
    /// Remediation: regenerate the response file in the isolated temp directory.
    /// </summary>
    ResponseFileIssue = 5,

    /// <summary>
    /// java.exe not found or JVM crashed at startup.
    /// Remediation: verify JDK is installed and JAVA_HOME is set correctly.
    /// </summary>
    JavaLaunchFailure = 6,

    /// <summary>
    /// OUI failed to extract the installer JAR (temp directory full or locked).
    /// Remediation: purge the extraction directory and retry with a clean temp path.
    /// </summary>
    ExtractionFailure = 7,

    /// <summary>
    /// OUI wrote partial artifacts to the middleware home but did not complete.
    /// Remediation: remove partial directory and inventory entry, then retry.
    /// </summary>
    PartialInstall = 8,
}

// ── Per-attempt execution context ─────────────────────────────────────────────

/// <summary>
/// Immutable context for a single OUI installer execution attempt.
///
/// Every retry gets a fresh <see cref="InstallerExecutionContext"/> with:
///   • Unique temp, extraction, and log directories (prevents state bleed across retries)
///   • Per-attempt response file and inventory pointer paths
///   • Previous failure classification (informs pre-flight remediation choices)
///   • Cleanup manifest (paths to delete after attempt, success or failure)
///
/// The context is constructed by <see cref="WEDM.Domain.Interfaces.IInstallRetryIsolationService"/>
/// and stored on <see cref="DeploymentConfiguration.CurrentInstallerContext"/> before each
/// OUI step is invoked.  OUI steps read it to configure their execution paths; they never
/// create directories or response files from config paths directly when a context is present.
/// </summary>
public sealed class InstallerExecutionContext
{
    /// <summary>1-based attempt number (1 = first attempt, 2 = first retry, …).</summary>
    public int  AttemptNumber { get; init; } = 1;

    /// <summary>Unique run identifier used to build all per-attempt path names.</summary>
    public Guid UniqueId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Isolated temporary directory for this attempt.
    /// Response files, inventory pointer, and scratch files are written here.
    /// </summary>
    public string TempDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Isolated JAR extraction directory.
    /// Passed to OUI as <c>-Djava.io.tmpdir</c> to prevent extraction artefacts
    /// from contaminating the system or shared temp directory.
    /// </summary>
    public string ExtractionDirectory { get; init; } = string.Empty;

    /// <summary>Absolute path of the generated .rsp response file for this attempt.</summary>
    public string ResponseFilePath { get; init; } = string.Empty;

    /// <summary>
    /// Absolute path of the WLS 11g silent XML file (null for 12c/14c).
    /// </summary>
    public string? SilentXmlPath { get; init; }

    /// <summary>
    /// Absolute path of the oraInst.loc inventory pointer written for this attempt.
    /// </summary>
    public string InventoryPointerPath { get; init; } = string.Empty;

    /// <summary>
    /// Directory where OUI log candidates are expected for this attempt.
    /// Used to scope the log scanner's search.
    /// </summary>
    public string OuiLogDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Failure classification from the PREVIOUS attempt (Unknown on first attempt).
    /// Used by the pre-flight validator to apply targeted remediation.
    /// </summary>
    public InstallerFailureClass PreviousFailureClass { get; init; } = InstallerFailureClass.Unknown;

    /// <summary>
    /// Paths created by this context that should be purged after the attempt completes
    /// (regardless of success/failure) to avoid accumulation of stale extraction artefacts.
    /// </summary>
    public IReadOnlyList<string> CleanupPaths { get; init; } = [];
}

// ── Pre-flight validation result ──────────────────────────────────────────────

/// <summary>
/// Result of the retry pre-flight validation performed before each OUI attempt.
/// </summary>
public sealed class InstallerRetryPreflightResult
{
    /// <summary>True when all pre-flight checks passed and OUI may safely launch.</summary>
    public bool CanProceed { get; init; }

    /// <summary>Human-readable summary of each check performed.</summary>
    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>Items that prevent launch (CanProceed will be false when this is non-empty).</summary>
    public IReadOnlyList<string> BlockingItems { get; init; } = [];

    /// <summary>Actions taken during preflight (cleanup performed, etc.).</summary>
    public IReadOnlyList<string> ActionsTaken { get; init; } = [];
}
