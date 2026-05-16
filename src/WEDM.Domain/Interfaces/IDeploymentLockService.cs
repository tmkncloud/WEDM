using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Machine-wide mutex for Oracle Home, domain, and central inventory paths.</summary>
public interface IDeploymentLockService
{
    Task<DeploymentLockAcquireResult> TryAcquireAsync(
        DeploymentConfiguration config,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task HeartbeatAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeploymentLockDescriptor>> ListActiveLocksAsync(CancellationToken cancellationToken = default);

    Task<int> CleanupStaleLocksAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
}

public sealed class DeploymentLockAcquireResult
{
    public bool Acquired { get; init; }
    public string? FailureReason { get; init; }
    public IReadOnlyList<DeploymentLockDescriptor> ConflictingLocks { get; init; } = [];
    public IReadOnlyList<DeploymentLockDescriptor> AcquiredLocks { get; init; } = [];
}
