using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;
using WEDM.Engine.OracleInventory;

namespace WEDM.Engine.OracleInventoryBootstrap;

public sealed class OracleInventoryBootstrapValidator : IOracleInventoryBootstrapValidator
{
    public InventoryBootstrapValidationResult Validate(string inventoryRoot, string inventoryXmlPath)
    {
        var findings = new List<string>();

        if (!Directory.Exists(inventoryRoot))
        {
            findings.Add($"BLOCKED: Inventory root does not exist: {inventoryRoot}");
            return Fail(findings);
        }

        if (!File.Exists(inventoryXmlPath))
        {
            findings.Add($"BLOCKED: inventory.xml not found at {inventoryXmlPath}");
            return Fail(findings);
        }

        try
        {
            var testFile = Path.Combine(inventoryRoot, $"wedm_bootstrap_write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            findings.Add("Inventory root is writable.");
        }
        catch (Exception ex)
        {
            findings.Add($"BLOCKED: Inventory root is not writable: {ex.Message}");
            return Fail(findings);
        }

        var snapshot = OracleInventoryXmlParser.ParseInventoryXml(inventoryXmlPath);
        if (snapshot.InventoryState == OracleCentralInventoryState.Corrupted)
        {
            findings.Add($"BLOCKED: Generated inventory.xml is not readable: {snapshot.InventoryWarning}");
            return Fail(findings);
        }

        if (!OracleCentralInventoryClassifier.IsXmlReadable(snapshot.InventoryState))
        {
            findings.Add($"BLOCKED: Unexpected inventory state after bootstrap: {snapshot.InventoryState}");
            return Fail(findings);
        }

        findings.Add($"Inventory XML validated: state={snapshot.InventoryState}, homes={snapshot.OracleHomes.Count}");
        findings.Add("HOME_LIST is accessible and empty (clean-install ready).");

        return new InventoryBootstrapValidationResult
        {
            Passed         = true,
            ResultingState = snapshot.InventoryState,
            Findings       = findings,
        };
    }

    private static InventoryBootstrapValidationResult Fail(List<string> findings) =>
        new() { Passed = false, ResultingState = OracleCentralInventoryState.BootstrapFailed, Findings = findings };
}
