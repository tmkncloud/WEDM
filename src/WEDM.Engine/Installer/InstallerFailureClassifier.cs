using WEDM.Domain.Models;

namespace WEDM.Engine.Installer;

/// <summary>
/// Classifies the root cause of an OUI installer failure from exit code and stderr output.
///
/// Classification order (most specific first):
///   1. Timeout (checked via the caller's timedOut flag)
///   2. Inventory conflict (INST-07319 / already registered)
///   3. Lock conflict (inventory lock active)
///   4. Java launch failure (java.exe not found / JVM crash)
///   5. Response file issue (bad path / parse error)
///   6. Prerequisite failure (disk, OS prereq)
///   7. Extraction failure (JAR extraction / temp dir full)
///   8. Partial install (OUI exited mid-write)
///   9. Unknown (everything else)
/// </summary>
public static class InstallerFailureClassifier
{
    // ── Signal strings per failure class ─────────────────────────────────────

    private static readonly string[] InventoryConflictSignals =
    [
        "INST-07319",
        "already registered",
        "Oracle Home already exists",
        "HOME already registered",
        "already installed",
        "target Oracle Home path is not empty",
    ];

    private static readonly string[] LockConflictSignals =
    [
        "orainventory.lock",
        "inventory is locked",
        "lock file exists",
        "Another installation in progress",
        "OUI-10170",    // inventory is locked by another process
    ];

    private static readonly string[] JavaLaunchSignals =
    [
        "java.exe not found",
        "java not found",
        "Could not find the main class",
        "Unable to access jarfile",
        "Error opening zip file",
        "JVM terminated",
        "Error occurred during initialization of VM",
    ];

    private static readonly string[] ResponseFileSignals =
    [
        "response file",
        "responseFile",
        "Response File",
        "RESP_FILE",
        "-silent requires",
        "OUI-10182",    // responseFile argument missing / malformed
        "OUI-10185",
    ];

    private static readonly string[] PrerequisiteSignals =
    [
        "Prerequisite",
        "prerequisite",
        "insufficient disk",
        "Disk Space",
        "disk space",
        "Missing dependency",
        "OUI-10133",    // prerequisites not met
        "OUI-10040",
        "kernel parameter",
        "PRIV-1539",
    ];

    private static readonly string[] ExtractionSignals =
    [
        "extracting",
        "extraction failed",
        "Cannot create temp file",
        "Unable to extract",
        "java.io.tmpdir",
        "No space left",
        "temp directory",
    ];

    private static readonly string[] PartialInstallSignals =
    [
        "rollback",
        "Rollback in progress",
        "Install failed after file copy",
        "partial installation",
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies the failure from installer exit code and combined stderr/stdout output.
    /// </summary>
    /// <param name="exitCode">Process exit code (negative codes are WEDM sentinels).</param>
    /// <param name="errorOutput">Combined stderr and OUI log output for signal matching.</param>
    /// <param name="timedOut">True when the installer was killed due to timeout.</param>
    /// <returns>The most specific matching <see cref="InstallerFailureClass"/>.</returns>
    public static InstallerFailureClass Classify(
        int    exitCode,
        string errorOutput,
        bool   timedOut = false)
    {
        if (timedOut)
            return InstallerFailureClass.Timeout;

        // WEDM internal sentinel codes
        if (exitCode == -2)     // timeout sentinel set in InstallWebLogicStep
            return InstallerFailureClass.Timeout;
        if (exitCode == -10)    // inventory pre-install check blocked OUI
            return InstallerFailureClass.InventoryConflict;

        var err = errorOutput ?? string.Empty;

        if (ContainsAny(err, InventoryConflictSignals))
            return InstallerFailureClass.InventoryConflict;

        if (ContainsAny(err, LockConflictSignals))
            return InstallerFailureClass.LockConflict;

        if (ContainsAny(err, JavaLaunchSignals))
            return InstallerFailureClass.JavaLaunchFailure;

        if (ContainsAny(err, ResponseFileSignals))
            return InstallerFailureClass.ResponseFileIssue;

        if (ContainsAny(err, PrerequisiteSignals))
            return InstallerFailureClass.PrerequisiteFailure;

        if (ContainsAny(err, ExtractionSignals))
            return InstallerFailureClass.ExtractionFailure;

        if (ContainsAny(err, PartialInstallSignals))
            return InstallerFailureClass.PartialInstall;

        return InstallerFailureClass.Unknown;
    }

    /// <summary>
    /// Returns a short human-readable remediation hint for a given failure class.
    /// Used in structured log output and retry telemetry.
    /// </summary>
    public static string GetRemediationHint(InstallerFailureClass failureClass) => failureClass switch
    {
        InstallerFailureClass.InventoryConflict  =>
            "Remove stale Central Inventory registration and partial MW home directory before retrying.",
        InstallerFailureClass.LockConflict =>
            "Wait for active Oracle operation to complete, or manually remove stale inventory lock file.",
        InstallerFailureClass.Timeout =>
            "Increase OuiInstallTimeoutMinutes (current default: 30 min). Check for hanging java.exe process.",
        InstallerFailureClass.PrerequisiteFailure =>
            "Check disk space under MW home and Temp directory. Verify VC++ redistributable is installed.",
        InstallerFailureClass.ResponseFileIssue =>
            "Response file will be regenerated in isolated temp directory on next attempt.",
        InstallerFailureClass.JavaLaunchFailure =>
            "Verify JDK is installed at the configured JavaHome path and java.exe is accessible.",
        InstallerFailureClass.ExtractionFailure =>
            "Purge OraInstall* directories in system temp. Each retry gets an isolated extraction directory.",
        InstallerFailureClass.PartialInstall =>
            "Remove partial MW home directory and deregister from Central Inventory before retrying.",
        _ =>
            "Check OUI log tail for further details.",
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool ContainsAny(string haystack, string[] needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));
}
