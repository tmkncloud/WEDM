using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Persists deployment session checkpoints for crash recovery and resume.</summary>
public interface IDeploymentSessionStore
{
    string RootDirectory { get; }

    Task SaveAsync(DeploymentSessionState state, CancellationToken cancellationToken = default);

    Task<DeploymentSessionState?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeploymentSessionState>> ListRecoverableAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
