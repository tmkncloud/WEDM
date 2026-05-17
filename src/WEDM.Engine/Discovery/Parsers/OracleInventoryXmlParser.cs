using System.Xml.Linq;
using WEDM.Domain.Models;
using WEDM.Engine.OracleInventory;

namespace WEDM.Engine.Discovery.Parsers;

public static class OracleInventoryXmlParser
{
    public static OracleInventorySnapshot ParseInventoryXml(string inventoryXmlPath)
    {
        var snapshot = new OracleInventorySnapshot { InventoryLoc = Path.GetDirectoryName(inventoryXmlPath) };
        if (!File.Exists(inventoryXmlPath))
        {
            snapshot.InventoryState    = OracleCentralInventoryState.Missing;
            snapshot.InventoryHealthy  = false;
            snapshot.InventoryWarning  = $"inventory.xml not found at {inventoryXmlPath}";
            return snapshot;
        }

        try
        {
            var doc = XDocument.Load(inventoryXmlPath);
            var homes = doc.Descendants("HOME").Concat(doc.Descendants("ORACLE_HOME"));
            foreach (var home in homes)
            {
                var loc = home.Attribute("LOC")?.Value ?? home.Element("LOCATION")?.Value;
                if (string.IsNullOrWhiteSpace(loc)) continue;
                snapshot.OracleHomes.Add(new OracleHomeDescriptor
                {
                    Path = loc.Trim(),
                    Name = home.Attribute("NAME")?.Value,
                });
            }

            foreach (var comp in doc.Descendants("COMP").Concat(doc.Descendants("COMPONENT")))
            {
                snapshot.InstalledProducts.Add(new InstalledProductDescriptor
                {
                    Name    = comp.Attribute("NAME")?.Value ?? comp.Element("NAME")?.Value ?? "Unknown",
                    Version = comp.Attribute("VER")?.Value ?? comp.Element("VERSION")?.Value,
                });
            }

            if (snapshot.OracleHomes.Count == 0)
            {
                snapshot.InventoryState   = OracleCentralInventoryState.Empty;
                snapshot.InventoryHealthy = true;
                snapshot.InventoryWarning = OracleCentralInventoryClassifier.EmptyInventoryMessage;
            }
            else
            {
                snapshot.InventoryState   = OracleCentralInventoryState.Healthy;
                snapshot.InventoryHealthy = true;
            }
        }
        catch (Exception ex)
        {
            snapshot.InventoryState   = OracleCentralInventoryState.Corrupted;
            snapshot.InventoryHealthy = false;
            snapshot.InventoryWarning = ex.Message;
        }

        return snapshot;
    }

    public static string? ResolveInventoryLocFromOraInst(string? middlewareHome)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(middlewareHome))
            candidates.Add(Path.Combine(middlewareHome, "oraInst.loc"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFiles, "Oracle", "Inventory", "oraInst.loc"));
        candidates.Add(@"C:\Oracle\oraInst.loc");

        foreach (var loc in candidates)
        {
            if (!File.Exists(loc)) continue;
            foreach (var line in File.ReadLines(loc))
            {
                if (line.StartsWith("inventory_loc", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.Split('=', 2).ElementAtOrDefault(1)?.Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }

        return null;
    }
}
