using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.OracleInventoryBootstrap;

public sealed class OracleInventorySkeletonFactory : IOracleInventorySkeletonFactory
{
    public string BuildInventoryXml(DeploymentConfiguration config, BootstrapVersionStrategy strategy)
    {
        var (savedWith, minimumVer) = ResolveVersionMetadata(config, strategy);
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <INVENTORY>
              <VERSION_INFO>
                <SAVED_WITH>{savedWith}</SAVED_WITH>
                <MINIMUM_VER>{minimumVer}</MINIMUM_VER>
              </VERSION_INFO>
              <HOME_LIST>
              </HOME_LIST>
            </INVENTORY>
            """;
    }

    public string GetVersionProfile(DeploymentConfiguration config, BootstrapVersionStrategy strategy)
    {
        var (savedWith, minimumVer) = ResolveVersionMetadata(config, strategy);
        return $"{savedWith} / min {minimumVer}";
    }

    private static (string SavedWith, string MinimumVer) ResolveVersionMetadata(
        DeploymentConfiguration config,
        BootstrapVersionStrategy strategy)
    {
        var version = strategy switch
        {
            BootstrapVersionStrategy.LatestSupported => WebLogicVersion.WLS_14c,
            BootstrapVersionStrategy.CompatibilityMode => WebLogicVersion.WLS_12c,
            BootstrapVersionStrategy.DerivedFromPayload => config.WebLogicVersion,
            _ => config.WebLogicVersion,
        };

        return version switch
        {
            WebLogicVersion.WLS_11g => ("10.3.6.0.0", "2.1.0.6.0"),
            WebLogicVersion.WLS_12c => ("12.2.0.1.4", "2.1.0.6.0"),
            WebLogicVersion.WLS_14c => ("14.1.1.0.0", "2.1.0.6.0"),
            WebLogicVersion.WLS_15c => ("15.0.0.0.0", "2.1.0.6.0"),
            _ => ("12.2.0.1.4", "2.1.0.6.0"),
        };
    }
}
