using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;
using WEDM.Engine.OracleInventory;

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
            analysis.State = OracleCentralInventoryState.Missing;
            analysis.XmlValid = false;
            analysis.CorruptionWarnings.Add("Inventory root not specified and could not be resolved from oraInst.loc.");
            return analysis;
        }

        analysis.InventoryRoot = inventoryRoot;

        if (TryDetectActiveLock(inventoryRoot, out var lockPath))
        {
            analysis.LockPresent  = true;
            analysis.LockFilePath = lockPath;
            analysis.State        = OracleCentralInventoryState.Locked;
            analysis.CorruptionWarnings.Add($"Oracle inventory lock detected: {lockPath}");
        }

        var centralXml = Path.Combine(inventoryRoot, "ContentsXML", "inventory.xml");
        if (!File.Exists(centralXml))
        {
            analysis.State = OracleCentralInventoryState.Missing;
            analysis.XmlValid = false;
            analysis.CorruptionWarnings.Add($"Central inventory.xml missing: {centralXml}");
            return analysis;
        }

        try
        {
            var snapshot = OracleInventoryXmlParser.ParseInventoryXml(centralXml);
            analysis.XmlValid = OracleCentralInventoryClassifier.IsXmlReadable(snapshot.InventoryState);

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

            if (snapshot.InventoryState == OracleCentralInventoryState.Corrupted)
            {
                analysis.State = OracleCentralInventoryState.Corrupted;
                analysis.XmlValid = false;
                analysis.CorruptionWarnings.Add(snapshot.InventoryWarning ?? "Central inventory.xml is corrupt or unreadable.");
            }
            else if (!analysis.LockPresent)
            {
                analysis.State = OracleCentralInventoryClassifier.RefineFromHomes(
                    snapshot.InventoryState,
                    analysis.Homes);
            }
        }
        catch (Exception ex)
        {
            analysis.State = OracleCentralInventoryState.Corrupted;
            analysis.XmlValid = false;
            analysis.CorruptionWarnings.Add($"Failed to parse inventory.xml: {ex.Message}");
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

                    if (!analysis.LockPresent && analysis.XmlValid)
                    {
                        analysis.State = OracleCentralInventoryClassifier.RefineFromHomes(
                            analysis.State == OracleCentralInventoryState.Empty
                                ? OracleCentralInventoryState.Empty
                                : OracleCentralInventoryState.Healthy,
                            analysis.Homes);
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

    private static bool TryDetectActiveLock(string inventoryRoot, out string? lockPath)
    {
        lockPath = null;
        var locksDir = Path.Combine(inventoryRoot, "locks");
        if (!Directory.Exists(locksDir))
            return false;

        foreach (var file in Directory.EnumerateFiles(locksDir, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(file);
            var age  = DateTimeOffset.UtcNow - info.LastWriteTimeUtc;
            if (age <= TimeSpan.FromHours(4))
            {
                lockPath = file;
                return true;
            }
        }

        return false;
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
                Actions    = [$"Would remove HOME entry for '{home}' from ContentsXML/inventory.xml"],
            });
        }

        var centralXml = Path.Combine(inventoryRoot, "ContentsXML", "inventory.xml");
        if (!File.Exists(centralXml))
        {
            return Task.FromResult(new InventoryDetachResult
            {
                Success    = false,
                OracleHome = home,
                Message    = $"Central inventory.xml not found at {centralXml}",
            });
        }

        try
        {
            var doc = System.Xml.Linq.XDocument.Load(centralXml);
            var removed = false;
            foreach (var homeEl in doc.Descendants("HOME").Concat(doc.Descendants("ORACLE_HOME")).ToList())
            {
                var loc = homeEl.Attribute("LOC")?.Value;
                if (loc is not null && loc.Trim().Equals(home, StringComparison.OrdinalIgnoreCase))
                {
                    homeEl.Remove();
                    removed = true;
                    actions.Add($"Removed HOME entry LOC='{loc}' from central inventory.");
                }
            }

            if (!removed)
            {
                return Task.FromResult(new InventoryDetachResult
                {
                    Success    = false,
                    OracleHome = home,
                    Message    = $"Home '{home}' was not found in central inventory.",
                });
            }

            doc.Save(centralXml);
            return Task.FromResult(new InventoryDetachResult
            {
                Success    = true,
                OracleHome = home,
                Message    = $"Detached Oracle Home '{home}' from central inventory.",
                Actions    = actions,
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new InventoryDetachResult
            {
                Success    = false,
                OracleHome = home,
                Message    = $"Failed to detach home: {ex.Message}",
            });
        }
    }
}
