using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Engine.Versioning.PayloadLayouts;

internal abstract class LocalPayloadLayoutBase : ILocalPayloadLayout
{
    public abstract string RepositoryFolderName { get; }

    public IReadOnlyList<LocalPayloadFolderDefinition> GetDefinitions(DeploymentConfiguration config)
        => BuildDefinitions(config).ToList();

    protected abstract IEnumerable<LocalPayloadFolderDefinition> BuildDefinitions(DeploymentConfiguration config);

    protected static LocalPayloadFolderDefinition Folder(
        LocalPayloadComponent component,
        string folder,
        bool required,
        params string[] patterns) => new()
    {
        Component     = component,
        FolderName    = folder,
        FilePatterns  = patterns,
        Required      = required
    };

    protected static LocalPayloadFolderDefinition Dir(
        LocalPayloadComponent component,
        string folder,
        bool required,
        params string[] subfolders) => new()
    {
        Component           = component,
        FolderName          = folder,
        Required            = required,
        DirectoryPayload    = true,
        RequiredSubfolders  = subfolders,
        FilePatterns        = []
    };
}
