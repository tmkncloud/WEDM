using System.Linq;
using WEDM.Domain.Models;

namespace WEDM.Engine.Opatch;

/// <summary>Resolves OPatch locations and inventory pointer paths for FMW / WebLogic homes on Windows.</summary>
public static class OpatchPaths
{
    public static string? ResolveOpatchBat(string oracleHome, string? explicitBatPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitBatPath) && File.Exists(explicitBatPath))
            return explicitBatPath;

        if (string.IsNullOrWhiteSpace(oracleHome) || !Directory.Exists(oracleHome))
            return null;

        var candidates = new[]
        {
            Path.Combine(oracleHome, "OPatch", "opatch.bat"),
            Path.Combine(oracleHome, "oracle_common", "OPatch", "opatch.bat"),
            Path.Combine(oracleHome, "wlserver", "OPatch", "opatch.bat"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string WriteOraInstPointer(DeploymentConfiguration config)
    {
        var ptr = Path.Combine(config.Paths.TempDirectory, "oraInst.loc");
        var content = $"inventory_loc={config.Paths.OracleInventory}\ninst_group=Administrators\n";
        Directory.CreateDirectory(config.Paths.TempDirectory);
        File.WriteAllText(ptr, content);
        return ptr;
    }
}
