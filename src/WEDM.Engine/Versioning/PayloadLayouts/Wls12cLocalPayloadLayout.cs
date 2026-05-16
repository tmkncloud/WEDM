using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Engine.Versioning.PayloadLayouts;

internal class Wls12cLocalPayloadLayout : LocalPayloadLayoutBase
{
    public override string RepositoryFolderName => "12c";

    protected override IEnumerable<LocalPayloadFolderDefinition> BuildDefinitions(DeploymentConfiguration config)
    {
        if (config.Components.HasFlag(InstallationComponents.JDK))
            yield return Folder(LocalPayloadComponent.Jdk, "jdk", true, "*.exe", "*.msi", "jdk*.exe");

        if (config.Components.HasFlag(InstallationComponents.VCRedist))
        {
            yield return Folder(LocalPayloadComponent.Vc, "vc", true, "vc_redist.x64.exe", "*x64*.exe", "vc_redist*.exe");
            yield return Folder(LocalPayloadComponent.Vc, "vc", false, "vc_redist.x86.exe", "*x86*.exe");
        }

        if (config.Components.HasFlag(InstallationComponents.Infrastructure))
            yield return Folder(LocalPayloadComponent.Infrastructure, "infrastructure", true,
                "*infrastructure*.jar", "fmw_*infrastructure*.jar");

        if (config.Components.HasFlag(InstallationComponents.WebLogicServer)
            && !config.Components.HasFlag(InstallationComponents.Infrastructure))
            yield return Folder(LocalPayloadComponent.WebLogic, "weblogic", true,
                "*wls*.jar", "*_wls_*.jar", "wls*.jar");

        if (config.Components.HasFlag(InstallationComponents.FormsReports))
        {
            yield return Folder(LocalPayloadComponent.Forms, "forms", true, "*fr*.exe", "setup*fr*.exe");
            if (config.Domain.FormsReports.InstallOhs)
                yield return Folder(LocalPayloadComponent.WebTier, "webtier", true, "*ohs*.exe", "setup*ohs*.exe");
            if (config.Domain.FormsReports.InstallWebUtil)
                yield return Dir(LocalPayloadComponent.WebUtil, "webutil", false, "java", "win32", "win64");
        }
    }
}
