using WEDM.Domain.Models;

namespace WEDM.Engine.Automation;

/// <summary>Applies conservative defaults per DEV/SIT/UAT/PROD without replacing user path overrides.</summary>
public static class EnvironmentProfilePresets
{
    public static void Apply(DeploymentConfiguration config, DeploymentEnvironmentKind kind)
    {
        config.DeploymentEnvironment = kind;
        switch (kind)
        {
            case DeploymentEnvironmentKind.Dev:
                config.DomainHardening.ProductionMode                  = false;
                config.DomainHardening.StrictPostValidation            = false;
                config.DomainHardening.RequireSecureListenAddresses     = false;
                config.DomainHardening.EnableAdministrationPort        = false;
                config.DomainHardening.SslPreparationOnly              = false;
                config.DomainOnlineAutomation.Enabled                 = false;
                config.DomainOnlineAutomation.StartAdminServerIfNotRunning = false;
                break;
            case DeploymentEnvironmentKind.Sit:
                config.DomainHardening.ProductionMode                  = false;
                config.DomainHardening.StrictPostValidation            = false;
                config.DomainHardening.RequireSecureListenAddresses     = false;
                config.DomainHardening.EnableAdministrationPort        = false;
                config.DomainOnlineAutomation.Enabled                 = true;
                config.DomainOnlineAutomation.StartAdminServerIfNotRunning = true;
                break;
            case DeploymentEnvironmentKind.Uat:
                config.DomainHardening.ProductionMode                  = true;
                config.DomainHardening.StrictPostValidation            = true;
                config.DomainHardening.RequireSecureListenAddresses     = true;
                config.DomainHardening.EnableAdministrationPort        = false;
                config.DomainOnlineAutomation.Enabled                 = true;
                config.DomainOnlineAutomation.StartAdminServerIfNotRunning = true;
                break;
            case DeploymentEnvironmentKind.Prod:
                config.DomainHardening.ProductionMode                  = true;
                config.DomainHardening.StrictPostValidation            = true;
                config.DomainHardening.RequireSecureListenAddresses     = true;
                config.DomainHardening.EnableAdministrationPort        = true;
                config.DomainHardening.SslPreparationOnly              = true;
                config.DomainOnlineAutomation.Enabled                 = true;
                config.DomainOnlineAutomation.StartAdminServerIfNotRunning = true;
                config.DomainOnlineAutomation.RunNmEnroll             = true;
                config.DomainOnlineAutomation.ApplyOnlineProductionAndMachineMapping = true;
                if (string.Equals(config.Domain.NodeManager.Type, "Plain", StringComparison.OrdinalIgnoreCase))
                    config.Domain.NodeManager.Type = "SSL";
                break;
        }
    }
}
