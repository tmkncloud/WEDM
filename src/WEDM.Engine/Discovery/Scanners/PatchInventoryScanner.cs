using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;
using WEDM.Engine.Opatch;

namespace WEDM.Engine.Discovery.Scanners;

public sealed class PatchInventoryScanner
{
    private readonly OpatchRunner _opatchRunner;

    public PatchInventoryScanner(OpatchRunner opatchRunner) => _opatchRunner = opatchRunner;

    public async Task<OracleInventorySnapshot> ScanAsync(
        string? middlewareHome,
        string? inventoryLoc,
        CancellationToken cancellationToken)
    {
        var snapshot = new OracleInventorySnapshot();

        inventoryLoc ??= OracleInventoryXmlParser.ResolveInventoryLocFromOraInst(middlewareHome);
        snapshot.InventoryLoc = inventoryLoc;

        if (!string.IsNullOrWhiteSpace(inventoryLoc))
        {
            var inventoryXml = Path.Combine(inventoryLoc, "ContentsXML", "inventory.xml");
            if (File.Exists(inventoryXml))
                snapshot = OracleInventoryXmlParser.ParseInventoryXml(inventoryXml);
        }

        if (!string.IsNullOrWhiteSpace(middlewareHome) && Directory.Exists(middlewareHome))
        {
            snapshot.OracleHomes.Add(new OracleHomeDescriptor { Path = middlewareHome, Name = "SCAN_TARGET_HOME" });

            var opatchBat = OpatchPaths.ResolveOpatchBat(middlewareHome, null);
            if (opatchBat is not null)
            {
                try
                {
                    var versionResult = await _opatchRunner.VersionForHomeAsync(middlewareHome, opatchBat, cancellationToken);
                    snapshot.OpatchVersion = versionResult.Output.Trim();

                    var invResult = await _opatchRunner.LsinventoryForHomeAsync(
                        middlewareHome, inventoryLoc, opatchBat, cancellationToken);
                    var parsed = OpatchInventoryParser.Parse(invResult.Output);
                    snapshot.Patches = parsed.Select(p => new PatchInventoryRecord
                    {
                        PatchId   = p.PatchId,
                        AppliedOn = p.AppliedOn,
                        Description = p.Description,
                    }).ToList();
                }
                catch
                {
                    snapshot.InventoryHealthy = false;
                }
            }
        }

        return snapshot;
    }
}
