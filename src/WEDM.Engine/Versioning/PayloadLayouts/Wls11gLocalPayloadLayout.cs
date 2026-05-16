using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Engine.Versioning.PayloadLayouts;

internal sealed class Wls11gLocalPayloadLayout : LocalPayloadLayoutBase
{
    public override string RepositoryFolderName => "11g";

    protected override IEnumerable<LocalPayloadFolderDefinition> BuildDefinitions(DeploymentConfiguration config)
    {
        if (config.Components.HasFlag(InstallationComponents.JDK))
            yield return Folder(LocalPayloadComponent.Jdk, "jdk", true, "*.exe", "*.msi", "jdk*.exe");

        if (config.Components.HasFlag(InstallationComponents.VCRedist))
            yield return Folder(LocalPayloadComponent.Vc, "vc", true, "*x64*.exe", "vc_redist*.exe");

        if (config.Components.HasFlag(InstallationComponents.WebLogicServer)
            || config.Components.HasFlag(InstallationComponents.Infrastructure))
            yield return Folder(LocalPayloadComponent.WebLogic, "weblogic", true,
                "wls*.jar", "*wls*.jar", "*generic*.jar");

        if (config.Components.HasFlag(InstallationComponents.FormsReports))
        {
            yield return Folder(LocalPayloadComponent.Forms, "forms", true, "*forms*.exe", "*fr*.exe");
            if (config.Domain.FormsReports.InstallOhs)
                yield return Folder(LocalPayloadComponent.WebTier, "webtier", false, "*ohs*.exe");
        }
    }
}
