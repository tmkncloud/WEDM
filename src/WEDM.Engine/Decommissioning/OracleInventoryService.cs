using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;

namespace WEDM.Engine.Decommissioning;

public sealed class OracleInventoryService : IOracleInventoryAnalyzer
{
    public OracleInventoryAnalysis Analyze(string? inventoryRoot, string? middlewareHome = null)
    {
        var analysis = new OracleInventoryAnalysis();

        if (string.IsNullOrWhiteSpace(inventoryRoot))
            inventoryRoot = OracleInventoryXmlParser.ResolveInventoryLocFromOraInst(middlewareHome);

        if (string.IsNullOrWhiteSpace(inventoryRoot))
        {
            analysis.CorruptionWarnings.Add("Inventory root not specified and could not be resolved from oraInst.loc.");
            return analysis;
        }

        analysis.InventoryRoot = inventoryRoot;

        var lockPath = Path.Combine(inventoryRoot, "locks");
        if (Directory.Exists(lockPath) && Directory.EnumerateFileSystemEntries(lockPath).Any())
        {
            analysis.LockPresent  = true;
            analysis.LockFilePath = lockPath;
            analysis.CorruptionWarnings.Add($"Inventory lock directory is not empty: {lockPath}");
        }

        var centralXml = Path.Combine(inventoryRoot, "ContentsXML", "inventory.xml");
        if (!File.Exists(centralXml))
        {
            analysis.CorruptionWarnings.Add($"Central inventory.xml missing: {centralXml}");
            analysis.XmlValid = false;
            return analysis;
        }

        try
        {
            var snapshot = OracleInventoryXmlParser.ParseInventoryXml(centralXml);
            analysis.XmlValid = snapshot.InventoryHealthy;

            foreach (var home in snapshot.OracleHomes)
            {
                var exists = Directory.Exists(home.Path);
                var localInv = Path.Combine(home.Path, "inventory");
                analysis.Homes.Add(new OracleInventoryHomeRecord
                {
                    Path                           = home.Path,
                    Name                           = home.Name,
                    PathExists                     = exists,
                    IsStale                        = !exists,
                    IsRegisteredInCentralInventory = true,
                    LocalInventoryPath             = Directory.Exists(localInv) ? localInv : null,
                    Issues                         = exists ? [] : ["Registered home path does not exist on disk (stale registration)."],
                });
            }

            if (!snapshot.InventoryHealthy)
                analysis.CorruptionWarnings.Add(snapshot.InventoryWarning ?? "Inventory XML parsed with warnings.");
        }
        catch (Exception ex)
        {
            analysis.CorruptionWarnings.Add($"Failed to parse inventory.xml: {ex.Message}");
            analysis.XmlValid = false;
        }

        if (!string.IsNullOrWhiteSpace(middlewareHome) && Directory.Exists(middlewareHome))
        {
            var localXml = Path.Combine(middlewareHome, "inventory", "ContentsXML", "inventory.xml");
            if (File.Exists(localXml))
            {
                try
                {
                    var local = OracleInventoryXmlParser.ParseInventoryXml(localXml);
                    foreach (var home in local.OracleHomes)
                    {
                        if (analysis.Homes.Any(h => h.Path.Equals(home.Path, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        analysis.Homes.Add(new OracleInventoryHomeRecord
                        {
                            Path               = home.Path,
                            Name               = home.Name,
                            PathExists         = Directory.Exists(home.Path),
                            IsStale            = !Directory.Exists(home.Path),
                            LocalInventoryPath = Path.GetDirectoryName(localXml),
                            Issues             = ["Found only in local inventory."],
                        });
                    }
                }
                catch (Exception ex)
                {
                    analysis.CorruptionWarnings.Add($"Local inventory parse failed: {ex.Message}");
                }
            }
        }

        return analysis;
    }

    public Task<InventoryDetachResult> DetachHomeAsync(
        string oracleHomePath,
        string inventoryRoot,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actions = new List<string>();
        var home = oracleHomePath.TrimEnd('\\');

        if (dryRun)
        {
            return Task.FromResult(new InventoryDetachResult
            {
                Success    = true,
                DryRun     = true,
                OracleHome = home,
                Message    = $"Dry-run: would detach Oracle Home '{home}' from inventory '{inventoryRoot}'.",
                Actions    = ["Simulate detachHome from central inventory", "Remove local inventory pointer if present"],
            });
        }

        var inventoryXml = Path.Combine(inventoryRoot, "ContentsXML", "inventory.xml");
        if (!File.Exists(inventoryXml))
        {
            return Task.FromResult(new InventoryDetachResult
            {
                Success    = false,
                OracleHome = home,
                Message    = $"Central inventory.xml not found: {inventoryXml}",
            });
        }

        try
        {
            var removed = RemoveHomeFromInventoryXml(inventoryXml, home, actions);
            return Task.FromResult(new InventoryDetachResult
            {
                Success    = removed,
                OracleHome = home,
                Message    = removed
                    ? $"Detached stale home registration for {home}."
                    : $"Home {home} was not found in central inventory.xml.",
                Actions    = actions,
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new InventoryDetachResult
            {
                Success    = false,
                OracleHome = home,
                Message    = ex.Message,
                Actions    = actions,
            });
        }
    }

    private static bool RemoveHomeFromInventoryXml(string inventoryXml, string homePath, List<string> actions)
    {
        var doc = System.Xml.Linq.XDocument.Load(inventoryXml);
        var homes = doc.Descendants("HOME").Concat(doc.Descendants("ORACLE_HOME")).ToList();
        var removed = false;

        foreach (var node in homes)
        {
            var loc = node.Attribute("LOC")?.Value ?? node.Element("LOCATION")?.Value;
            if (string.IsNullOrWhiteSpace(loc)) continue;
            if (!loc.Trim().Equals(homePath, StringComparison.OrdinalIgnoreCase)) continue;
            node.Remove();
            removed = true;
            actions.Add($"Removed HOME LOC={loc} from {inventoryXml}");
        }

        if (removed)
            doc.Save(inventoryXml);

        return removed;
    }
}
