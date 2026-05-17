using FluentAssertions;
using WEDM.Domain.Models;
using WEDM.Engine.Decommissioning;
using WEDM.Engine.Jdk;
using WEDM.Engine.Tests.Fakes;
using WEDM.Infrastructure.Registry;
using Xunit;

namespace WEDM.Engine.Tests.Decommissioning;

public sealed class DecommissionSubsystemTests
{
    [Fact]
    public void DeployOracleConflictDetector_flags_non_empty_middleware_home()
    {
        var log = new FakeLoggingService();
        var inventory = new OracleInventoryService();
        var processes = new OracleProcessManager();
        var validator = new OracleHomeValidator(inventory, processes);
        var detector = new DeployOracleConflictDetector(inventory, validator);

        var temp = Path.Combine(Path.GetTempPath(), "wedm_mw_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "wlserver"));

        try
        {
            var config = new DeploymentConfiguration
            {
                Paths = new PathConfiguration { MiddlewareHome = temp, OracleInventory = temp },
            };

            var report = detector.DetectConflicts(config);
            report.HasBlockingConflicts.Should().BeTrue();
            report.SuggestDecommission.Should().BeTrue();
            report.Findings.Should().Contain(f => f.Code.Contains("OracleHome"));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void InstallRetryIsolation_creates_unique_temp_per_attempt()
    {
        var log = new FakeLoggingService();
        var rsp = new WEDM.Engine.ResponseFiles.ResponseFileGenerator(new FakeLoggingService());
        var cleanup = new OracleCleanupService(log);
        var inventory = new WEDM.Engine.OracleInventory.OracleInventoryService(log);
        var isolation = new InstallRetryIsolationService(log, rsp, cleanup, inventory);

        var config = new DeploymentConfiguration
        {
            Paths = new PathConfiguration { TempDirectory = Path.Combine(Path.GetTempPath(), "wedm_retry_" + Guid.NewGuid().ToString("N")) },
            OracleLifecycle = new OracleLifecycleConfiguration { IsolateRetries = true },
        };
        Directory.CreateDirectory(config.Paths.TempDirectory);

        var ctx1 = isolation.PrepareRetryAttempt(config, "InstallInfrastructure", 2);
        var ctx2 = isolation.PrepareRetryAttempt(config, "InstallInfrastructure", 3);

        ctx1.IsolatedTempDirectory.Should().NotBe(ctx2.IsolatedTempDirectory);
        Directory.Exists(ctx1.IsolatedTempDirectory).Should().BeTrue();
    }

    [Fact]
    public void DecommissionWorkflow_builds_nine_steps()
    {
        var engine = new DecommissionWorkflowEngine(
            new FakeLoggingService(),
            null!, null!, null!, null!, null!, null!, null!, null!, null!);

        var plan = engine.BuildStepPlan(new DecommissionConfiguration());
        plan.Should().HaveCount(9);
        plan.Select(s => s.Name).Should().Contain("DecommissionDiscover");
        plan.Select(s => s.Name).Should().Contain("DecommissionInventoryDetach");
    }

    [Fact]
    public void DeployOracleConflictDetector_allows_empty_central_inventory()
    {
        var temp = Path.Combine(Path.GetTempPath(), "wedm_empty_inv_" + Guid.NewGuid().ToString("N"));
        var contents = Path.Combine(temp, "ContentsXML");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "inventory.xml"), """
            <?xml version="1.0" standalone="yes"?>
            <INVENTORY><HOME_LIST/></INVENTORY>
            """);

        var mw = Path.Combine(Path.GetTempPath(), "wedm_mw_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mw);

        try
        {
            var inventory = new OracleInventoryService();
            var detector = new DeployOracleConflictDetector(
                inventory,
                new OracleHomeValidator(inventory, new OracleProcessManager()));

            var report = detector.DetectConflicts(new DeploymentConfiguration
            {
                Paths = new PathConfiguration { MiddlewareHome = mw, OracleInventory = temp },
            });

            report.HasBlockingConflicts.Should().BeFalse();
            report.Findings.Should().Contain(f => f.Code == "Inventory.Empty");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            Directory.Delete(mw, recursive: true);
        }
    }

    [Fact]
    public void OracleInventoryService_detects_stale_home_registration()
    {
        var temp = Path.Combine(Path.GetTempPath(), "wedm_inv_" + Guid.NewGuid().ToString("N"));
        var contents = Path.Combine(temp, "ContentsXML");
        Directory.CreateDirectory(contents);

        var xml = """
            <?xml version="1.0" standalone="yes"?>
            <INVENTORY>
              <HOME_LIST>
                <HOME LOC="D:\\Oracle\\MissingHome" NAME="missing"/>
              </HOME_LIST>
            </INVENTORY>
            """;
        File.WriteAllText(Path.Combine(contents, "inventory.xml"), xml);

        try
        {
            var svc = new OracleInventoryService();
            var analysis = svc.Analyze(temp);
            analysis.Homes.Should().ContainSingle(h => h.IsStale);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
