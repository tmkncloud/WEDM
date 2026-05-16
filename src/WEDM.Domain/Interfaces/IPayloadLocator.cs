using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Resolves Oracle installers from the fixed local repository layout (D:\WEDM\{11g|12c|14c|15c}\...).
/// </summary>
public interface IPayloadLocator
{
    string GetRepositoryRoot(DeploymentConfiguration config);

    string GetVersionFolderPath(DeploymentConfiguration config);

    /// <summary>Validate all required/optional payloads and populate <see cref="DeploymentConfiguration.LocalPayload"/>.</summary>
    Task<LocalPayloadRepositoryReport> ValidateAndResolveAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default);

    PayloadResolutionResult Resolve(
        LocalPayloadComponent component,
        DeploymentConfiguration config);

    void ApplyResolvedPathsToConfiguration(DeploymentConfiguration config, LocalPayloadRepositoryReport report);
}
