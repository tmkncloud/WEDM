using FluentAssertions;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Decommissioning;
using WEDM.Engine.Discovery.Parsers;
using WEDM.Engine.OracleInventory;
using Xunit;

namespace WEDM.Engine.Tests.OracleInventory;

public sealed class OracleCentralInventoryValidationTests : IDisposable
{
    private readonly string _root;
    private bool _disposed;

    public OracleCentralInventoryValidationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"WEDM_InvVal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private string CreateCentralInventory(string xmlContent)
    {
        var invRoot = Path.Combine(_root, "oraInventory");
        var dir     = Path.Combine(invRoot, "ContentsXML");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "inventory.xml"), xmlContent);
        return invRoot;
    }

    private static string EmptyHomeListXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <INVENTORY>
          <VERSION_INFO>
            <SAVED_WITH>11.2.0.3.0</SAVED_WITH>
          </VERSION_INFO>
          <HOME_LIST/>
        </INVENTORY>
        """;

    private static string SingleHomeXml(string loc) =>
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <INVENTORY>
          <HOME_LIST>
            <HOME NAME="OraHome1" LOC="{loc}" TYPE="O" IDX="1"/>
          </HOME_LIST>
        </INVENTORY>
        """;

    [Fact]
    public void Parser_empty_home_list_is_clean_install_state()
    {
        var inv = CreateCentralInventory(EmptyHomeListXml());
        var xml = Path.Combine(inv, "ContentsXML", "inventory.xml");

        var snapshot = OracleInventoryXmlParser.ParseInventoryXml(xml);

        snapshot.InventoryState.Should().Be(OracleCentralInventoryState.Empty);
        snapshot.InventoryHealthy.Should().BeTrue();
        snapshot.InventoryWarning.Should().Be(OracleCentralInventoryClassifier.EmptyInventoryMessage);
        snapshot.OracleHomes.Should().BeEmpty();
    }

    [Fact]
    public void Parser_missing_file_is_fatal_state()
    {
        var snapshot = OracleInventoryXmlParser.ParseInventoryXml(
            Path.Combine(_root, "missing", "inventory.xml"));

        snapshot.InventoryState.Should().Be(OracleCentralInventoryState.Missing);
        snapshot.InventoryHealthy.Should().BeFalse();
    }

    [Fact]
    public void Parser_malformed_xml_is_corrupted_state()
    {
        var inv = CreateCentralInventory("<INVENTORY><HOME_LIST>");
        var xml = Path.Combine(inv, "ContentsXML", "inventory.xml");

        var snapshot = OracleInventoryXmlParser.ParseInventoryXml(xml);

        snapshot.InventoryState.Should().Be(OracleCentralInventoryState.Corrupted);
        snapshot.InventoryHealthy.Should().BeFalse();
    }

    [Fact]
    public void Analyzer_empty_inventory_does_not_mark_xml_invalid()
    {
        var inv      = CreateCentralInventory(EmptyHomeListXml());
        var analysis = new WEDM.Engine.Decommissioning.OracleInventoryService().Analyze(inv);

        analysis.State.Should().Be(OracleCentralInventoryState.Empty);
        analysis.XmlValid.Should().BeTrue();
        analysis.CorruptionWarnings.Should().BeEmpty();
    }

    [Fact]
    public void Analyzer_missing_inventory_xml_is_blocking_state()
    {
        var invOnly = Path.Combine(_root, "no_xml");
        Directory.CreateDirectory(Path.Combine(invOnly, "ContentsXML"));

        var analysis = new WEDM.Engine.Decommissioning.OracleInventoryService().Analyze(invOnly);

        analysis.State.Should().Be(OracleCentralInventoryState.Missing);
        analysis.XmlValid.Should().BeFalse();
    }

    [Fact]
    public void Analyzer_stale_home_registration_is_stale_state()
    {
        var inv      = CreateCentralInventory(SingleHomeXml(@"D:\Oracle\MissingHome"));
        var analysis = new WEDM.Engine.Decommissioning.OracleInventoryService().Analyze(inv);

        analysis.State.Should().Be(OracleCentralInventoryState.Stale);
        analysis.XmlValid.Should().BeTrue();
        analysis.Homes.Should().ContainSingle(h => h.IsStale);
    }

    [Fact]
    public void Analyzer_active_lock_sets_locked_state()
    {
        var inv = CreateCentralInventory(EmptyHomeListXml());
        var locksDir = Path.Combine(inv, "locks");
        Directory.CreateDirectory(locksDir);
        File.WriteAllText(Path.Combine(locksDir, "install.lock"), "locked");

        var analysis = new WEDM.Engine.Decommissioning.OracleInventoryService().Analyze(inv);

        analysis.State.Should().Be(OracleCentralInventoryState.Locked);
        analysis.LockPresent.Should().BeTrue();
    }

    [Fact]
    public void ConflictDetector_empty_inventory_is_informational_not_blocking()
    {
        var inv       = CreateCentralInventory(EmptyHomeListXml());
        var mw        = Path.Combine(_root, "middleware");
        Directory.CreateDirectory(mw);
        var inventory = new WEDM.Engine.Decommissioning.OracleInventoryService();
        var detector  = new DeployOracleConflictDetector(inventory, new OracleHomeValidator(inventory, new OracleProcessManager()));

        var report = detector.DetectConflicts(new DeploymentConfiguration
        {
            Paths = new PathConfiguration { MiddlewareHome = mw, OracleInventory = inv },
        });

        report.HasBlockingConflicts.Should().BeFalse();
        report.Findings.Should().Contain(f =>
            f.Code == "Inventory.Empty" && f.Severity == OracleConflictSeverity.Informational);
        report.Findings.Should().NotContain(f => f.Code == "Inventory.Corrupt");
    }

    [Fact]
    public void ConflictDetector_missing_inventory_xml_is_fatal_error()
    {
        var invOnly = Path.Combine(_root, "missing_xml");
        Directory.CreateDirectory(Path.Combine(invOnly, "ContentsXML"));
        var mw        = Path.Combine(_root, "mw2");
        Directory.CreateDirectory(mw);
        var inventory = new WEDM.Engine.Decommissioning.OracleInventoryService();
        var detector  = new DeployOracleConflictDetector(inventory, new OracleHomeValidator(inventory, new OracleProcessManager()));

        var report = detector.DetectConflicts(new DeploymentConfiguration
        {
            Paths = new PathConfiguration { MiddlewareHome = mw, OracleInventory = invOnly },
        });

        report.Findings.Should().Contain(f => f.Code == "Inventory.Missing");
    }

    [Fact]
    public void InstallInventoryService_validateForInstall_allows_empty_inventory()
    {
        var inv = CreateCentralInventory(EmptyHomeListXml());
        var mw  = Path.Combine(_root, "mw3");
        var svc = new WEDM.Engine.OracleInventory.OracleInventoryService(new WEDM.Engine.Tests.Fakes.FakeLoggingService());

        var result = svc.ValidateForInstall(mw, inv);

        result.CanProceed.Should().BeTrue();
        result.Findings.Should().Contain(f => f.Contains(OracleCentralInventoryClassifier.EmptyInventoryMessage));
    }

    [Fact]
    public void InstallInventoryService_readSnapshot_empty_home_list_is_healthy()
    {
        var inv = CreateCentralInventory(EmptyHomeListXml());
        var svc = new WEDM.Engine.OracleInventory.OracleInventoryService(new WEDM.Engine.Tests.Fakes.FakeLoggingService());

        var snapshot = svc.ReadSnapshot(inv);

        snapshot.Should().NotBeNull();
        snapshot!.InventoryState.Should().Be(OracleCentralInventoryState.Empty);
        snapshot.InventoryHealthy.Should().BeTrue();
    }
}
