using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Reads update manifests from local paths only (no network).</summary>
public interface IUpdateManifestReader
{
    Task<WedmUpdateManifest?> TryReadLocalAsync(string manifestFilePath, CancellationToken cancellationToken = default);
}
