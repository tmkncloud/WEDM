using FluentAssertions;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Decommissioning;
using WEDM.Engine.Discovery.Parsers;
using WEDM.Engine.OracleInventory;
using WEDM.Engine.OracleInventoryBootstrap;
using WEDM.Engine.Tests.Fakes;
using Xunit;

namespace WEDM.Engine.Tests.OracleInventoryBootstrap;

public sealed class OracleInventoryBootstrapTests : IDisposable
{
    private readonly string _root;
    private bool _disposed;

    public OracleInventoryBootstrapTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"WEDM_Boot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private (IOracleInventoryBootstrapService svc, DeploymentConfiguration config) BuildSut(WebLogicVersion version = WebLogicVersion.WLS_12c)
    {
        var log       = new FakeLoggingService();
        var inventory = new WEDM.Engine.OracleInventory.OracleInventoryService(log);
        var analyzer  = new WEDM.Engine.Decommissioning.OracleInventoryService();
        var svc = new OracleInventoryBootstrapService(
            analyzer,
            new OracleInventoryPathResolver(),
            new OracleInventorySkeletonFactory(),
            new OracleInventoryBootstrapValidator(),
            new OracleInventoryBootstrapReportBuilder(),
            new OracleProcessManager(),
            inventory,
            log);

        var invRoot = Path.Combine(_root, "oraInventory");
        var config = new DeploymentConfiguration
        {
            WebLogicVersion = version,
            Paths = new PathConfiguration
            {
                MiddlewareHome  = Path.Combine(_root, "middleware"),
                OracleInventory = invRoot,
                TempDirectory   = Path.Combine(_root, "temp"),
                ReportsDirectory = Path.Combine(_root, "reports"),
            },
            OracleLifecycle = new OracleLifecycleConfiguration
            {
                EnableAutomaticInventoryBootstrap = true,
                AllowBootstrapOnCleanInstall      = true,
                BootstrapVersionStrategy          = BootstrapVersionStrategy.VersionSpecific,
            },
        };
        Directory.CreateDirectory(config.Paths.TempDirectory);
        Directory.CreateDirectory(config.Paths.ReportsDirectory);
        return (svc, config);
    }

    [Fact]
    public void Assess_detects_bootstrap_required_when_inventory_xml_missing()
    {
        var (svc, config) = BuildSut();
        var assessment = svc.Assess(config);

        assessment.State.Should().Be(OracleCentralInventoryState.BootstrapRequired);
        assessment.RequiresBootstrap.Should().BeTrue();
        assessment.CanAutoBootstrap.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureInventoryReady_creates_inventory_xml_on_clean_server()
    {
        var (svc, config) = BuildSut();
        var result = await svc.EnsureInventoryReadyAsync(
            config,
            new InventoryBootstrapExecutionOptions { Trigger = "Test" });

        var xml = Path.Combine(config.Paths.OracleInventory, "ContentsXML", "inventory.xml");
        File.Exists(xml).Should().BeTrue();
        result.Success.Should().BeTrue();

        var snapshot = OracleInventoryXmlParser.ParseInventoryXml(xml);
        snapshot.InventoryState.Should().Be(OracleCentralInventoryState.Empty);
        snapshot.OracleHomes.Should().BeEmpty();
    }

    [Fact]
    public async Task Dry_run_does_not_create_files()
    {
        var (svc, config) = BuildSut();
        var xml = Path.Combine(config.Paths.OracleInventory, "ContentsXML", "inventory.xml");

        var result = await svc.EnsureInventoryReadyAsync(
            config,
            new InventoryBootstrapExecutionOptions { DryRun = true, Trigger = "Test" });

        File.Exists(xml).Should().BeFalse();
        result.Report.DryRun.Should().BeTrue();
    }

    [Fact]
    public async Task Bootstrap_is_idempotent_when_inventory_already_exists()
    {
        var (svc, config) = BuildSut();
        await svc.EnsureInventoryReadyAsync(config, new InventoryBootstrapExecutionOptions { Trigger = "T1" });
        var result2 = await svc.EnsureInventoryReadyAsync(config, new InventoryBootstrapExecutionOptions { Trigger = "T2" });
        result2.Success.Should().BeTrue();
    }

    [Fact]
    public void SkeletonFactory_uses_version_specific_metadata_for_11g()
    {
        var factory = new OracleInventorySkeletonFactory();
        var config  = new DeploymentConfiguration { WebLogicVersion = WebLogicVersion.WLS_11g };
        var xml     = factory.BuildInventoryXml(config, BootstrapVersionStrategy.VersionSpecific);
        xml.Should().Contain("10.3.6.0.0");
    }

    [Fact]
    public void Bootstrap_refuses_when_corrupt_inventory_exists()
    {
        var (svc, config) = BuildSut();
        var contents = Path.Combine(config.Paths.OracleInventory, "ContentsXML");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "inventory.xml"), "<broken>");

        var assessment = svc.Assess(config);
        assessment.Safety.IsSafe.Should().BeFalse();
    }
}
