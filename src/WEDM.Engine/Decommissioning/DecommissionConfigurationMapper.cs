using WEDM.Domain.Models;

namespace WEDM.Engine.Decommissioning;

/// <summary>Maps decommission configuration to deployment configuration for reuse of install rollback steps.</summary>
public static class DecommissionConfigurationMapper
{
    public static DeploymentConfiguration ToDeployment(DecommissionConfiguration source)
    {
        return new DeploymentConfiguration
        {
            Name        = source.Name,
            EnableRollback = false,
            Paths = new PathConfiguration
            {
                OracleRoot        = source.Paths.OracleRoot,
                MiddlewareHome    = source.Paths.MiddlewareHome,
                DomainBase        = source.Paths.DomainBase,
                OracleInventory   = source.Paths.OracleInventory,
                TempDirectory     = source.Paths.TempDirectory,
                LogDirectory      = source.Paths.LogDirectory,
                ReportsDirectory  = source.Paths.ReportsDirectory,
                SnapshotDirectory = source.Paths.SnapshotDirectory,
            },
            Java = new JavaConfiguration
            {
                JavaHome         = source.Paths.JavaHome ?? string.Empty,
                InstallDirectory = source.Paths.JavaHome is not null
                    ? Path.GetDirectoryName(source.Paths.JavaHome.TrimEnd('\\')) ?? @"C:\Program Files\Java"
                    : @"C:\Program Files\Java",
            },
            Database = new DatabaseConfiguration
            {
                RunRcu        = source.Options.DropRcuSchemas,
                SchemaPrefix  = source.Options.RcuPrefix ?? "DEV",
            },
        };
    }
}
