using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Detects, downloads, and resolves JDK / VC++ installer payloads for deployment.</summary>
public interface IPayloadAcquisitionService
{
    Task<PrerequisiteValidationResult> ValidateAndPrepareAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);

    bool TryDetectCompatibleJdk(DeploymentConfiguration config, out string? javaHome);

    bool IsVcRedistInstalled();

    Task<PayloadResolutionResult> EnsureJdkInstallerAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);

    Task<PayloadResolutionResult> EnsureVcRedistInstallerAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);
}

public enum PayloadResolutionStatus
{
    NotRequired,
    AlreadyInstalled,
    ResolvedExisting,
    Downloaded,
    Failed
}

public sealed class PayloadResolutionResult
{
    public PayloadResolutionStatus Status { get; init; }
    public string? InstallerPath { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Success => Status is PayloadResolutionStatus.NotRequired
        or PayloadResolutionStatus.AlreadyInstalled
        or PayloadResolutionStatus.ResolvedExisting
        or PayloadResolutionStatus.Downloaded;
}
