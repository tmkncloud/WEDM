using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Contract for the prerequisite validation engine.
/// Implementations run all system checks before any installation begins.
/// </summary>
public interface IValidationEngine
{
    /// <summary>Run the full prerequisite validation suite.</summary>
    Task<PrerequisiteValidationResult> ValidateAllAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);

    /// <summary>Run only OS-level checks (version, architecture, activation).</summary>
    Task<PrerequisiteValidationResult> ValidateOperatingSystemAsync(CancellationToken ct = default);

    /// <summary>Check RAM and CPU resources meet WebLogic minimums.</summary>
    Task<PrerequisiteValidationResult> ValidateHardwareAsync(
        DeploymentConfiguration config, CancellationToken ct = default);

    /// <summary>Verify disk space on the target drives.</summary>
    Task<PrerequisiteValidationResult> ValidateDiskSpaceAsync(
        DeploymentConfiguration config, CancellationToken ct = default);

    /// <summary>Check whether required TCP ports are available (not in use).</summary>
    Task<PrerequisiteValidationResult> ValidatePortsAsync(
        DeploymentConfiguration config, CancellationToken ct = default);

    /// <summary>Verify current process has Administrator privileges.</summary>
    Task<PrerequisiteValidationResult> ValidatePrivilegesAsync(CancellationToken ct = default);

    /// <summary>Detect and validate existing JDK installation or planned JDK installer.</summary>
    Task<PrerequisiteValidationResult> ValidateJavaAsync(
        DeploymentConfiguration config, CancellationToken ct = default);

    /// <summary>Check VC++ redistributable presence.</summary>
    Task<PrerequisiteValidationResult> ValidateVcRedistAsync(CancellationToken ct = default);

    /// <summary>Verify all payload binary files are present and SHA-256 checksums match.</summary>
    Task<PrerequisiteValidationResult> ValidatePayloadIntegrityAsync(
        DeploymentConfiguration config, CancellationToken ct = default);

    /// <summary>Test Oracle DB connectivity (if RCU is required).</summary>
    Task<PrerequisiteValidationResult> ValidateDatabaseConnectivityAsync(
        DeploymentConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Reduced validation for OPatch-only workflows: admin, disk, Oracle Home, OPatch binary, staging path.
    /// </summary>
    Task<PrerequisiteValidationResult> ValidateForPatchingAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);
}
