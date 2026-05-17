using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;

namespace WEDM.Engine.OracleInventoryBootstrap;

public sealed class OracleInventoryPathResolver : IOracleInventoryPathResolver
{
    public InventoryPointerContext Resolve(DeploymentConfiguration config, InventoryPointerScope scope = InventoryPointerScope.DefaultCentral)
    {
        var root = ResolveInventoryRoot(config);
        var pointerPath = ResolvePointerPath(config, scope);

        return new InventoryPointerContext
        {
            CentralInventoryRoot = root,
            PointerFilePath      = pointerPath,
            Scope                = scope,
            IsIsolated           = scope is InventoryPointerScope.RetryIsolation or InventoryPointerScope.Temporary,
        };
    }

    internal static string ResolveInventoryRoot(DeploymentConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.Paths.OracleInventory))
            return config.Paths.OracleInventory.TrimEnd('\\');

        var fromOraInst = OracleInventoryXmlParser.ResolveInventoryLocFromOraInst(config.Paths.MiddlewareHome);
        return fromOraInst?.TrimEnd('\\') ?? string.Empty;
    }

    private static string ResolvePointerPath(DeploymentConfiguration config, InventoryPointerScope scope)
    {
        var ctx = config.CurrentInstallerContext;
        if (scope == InventoryPointerScope.RetryIsolation && !string.IsNullOrWhiteSpace(ctx?.InventoryPointerPath))
            return ctx.InventoryPointerPath;

        if (scope == InventoryPointerScope.RetryIsolation && !string.IsNullOrWhiteSpace(ctx?.TempDirectory))
            return Path.Combine(ctx.TempDirectory, "oraInst.loc");

        if (!string.IsNullOrWhiteSpace(config.Paths.TempDirectory))
            return Path.Combine(config.Paths.TempDirectory, "oraInst.loc");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WEDM", "oraInst.loc");
    }
}
