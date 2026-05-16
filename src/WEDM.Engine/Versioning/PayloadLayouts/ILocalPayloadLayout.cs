using WEDM.Domain.Models;

namespace WEDM.Engine.Versioning.PayloadLayouts;

/// <summary>Version-specific local repository folder layout under D:\WEDM\{folder}.</summary>
public interface ILocalPayloadLayout
{
    string RepositoryFolderName { get; }

    IReadOnlyList<LocalPayloadFolderDefinition> GetDefinitions(DeploymentConfiguration config);
}
