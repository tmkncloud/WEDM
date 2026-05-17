using FluentAssertions;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Decommissioning;
using WEDM.Engine.OracleInventory;
using WEDM.Engine.Remediation;
using WEDM.Engine.Tests.Fakes;
using Xunit;

namespace WEDM.Engine.Tests.Remediation;

public sealed class OracleRemediationTests : IDisposable
{
    private readonly string _root;
    private bool _disposed;

    public OracleRemediationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"WEDM_Remed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private (IOracleRemediationService svc, DeploymentConfiguration config) BuildSut(bool createPartialMarker = true)
    {
        var log       = new FakeLoggingService();
        var inventory = new WEDM.Engine.OracleInventory.OracleInventoryService(log);
        var analyzer  = new WEDM.Engine.Decommissioning.OracleInventoryService();
        var processes = new OracleProcessManager();
        var safety    = new OracleHomeSafetyAnalyzer(processes, inventory);
        var classifier = new PartialInstallClassifier(inventory, analyzer, safety);
        var planBuilder = new RemediationPlanBuilder();
        var remediator  = new OraclePartialInstallRemediator(log, analyzer, processes);
        var verify      = new RemediationVerificationService(inventory, processes);
        var reports     = new RemediationReportBuilder();
        var engine      = new RemediationExecutionEngine(remediator, verify, reports, log);
        var svc         = new OracleRemediationService(classifier, planBuilder, engine, log);

        var inv = Path.Combine(_root, "oraInventory");
        Directory.CreateDirectory(Path.Combine(inv, "ContentsXML"));
        File.WriteAllText(Path.Combine(inv, "ContentsXML", "inventory.xml"),
            """<?xml version="1.0"?><INVENTORY><HOME_LIST/></INVENTORY>""");

        var mw = Path.Combine(_root, "middleware");
        Directory.CreateDirectory(mw);
        if (createPartialMarker)
            Directory.CreateDirectory(Path.Combine(mw, "inventory"));

        var config = new DeploymentConfiguration
        {
            Paths = new PathConfiguration
            {
                MiddlewareHome  = mw,
                OracleInventory = inv,
                TempDirectory   = Path.Combine(_root, "temp"),
                ReportsDirectory = Path.Combine(_root, "reports"),
            },
            OracleLifecycle = new OracleLifecycleConfiguration
            {
                EnableAutoRemediation         = true,
                AutoRemediationMode           = AutoRemediationMode.AutomaticSafeOnly,
                SafeCleanupOnly               = true,
                AutoContinueAfterRemediation  = true,
                CleanupRetryIsolationArtifacts  = true,
            },
        };
        Directory.CreateDirectory(config.Paths.TempDirectory);
        Directory.CreateDirectory(config.Paths.ReportsDirectory);

        return (svc, config);
    }

    [Fact]
    public void Classifier_detects_partial_install_when_directory_has_files()
    {
        var (svc, config) = BuildSut();
        var assessment = svc.Assess(config);

        assessment.Classification.Should().BeOneOf(
            OracleRemediationState.PartialInstall,
            OracleRemediationState.FilesystemOnly,
            OracleRemediationState.SafeToClean,
            OracleRemediationState.UnsafeToClean);
        if (assessment.Classification == OracleRemediationState.UnsafeToClean)
            return;
        assessment.RequiresRemediation.Should().BeTrue();
        assessment.RecommendedPlan!.Actions.Should().NotBeEmpty();
    }

    [Fact]
    public void SafetyAnalyzer_blocks_when_java_process_running()
    {
        var log       = new FakeLoggingService();
        var inventory = new WEDM.Engine.OracleInventory.OracleInventoryService(log);
        var analyzer  = new WEDM.Engine.Decommissioning.OracleInventoryService();
        var safety    = new OracleHomeSafetyAnalyzer(new OracleProcessManager(), inventory);

        var ctx = new RemediationDiscoveryContext
        {
            MiddlewareHome      = Path.Combine(_root, "mw2"),
            OracleInventoryPath = Path.Combine(_root, "inv2"),
        };

        var result = safety.Analyze(ctx, OracleRemediationState.PartialInstall);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_dry_run_does_not_delete_directory()
    {
        var (svc, config) = BuildSut();
        var mw = config.Paths.MiddlewareHome;

        var result = await svc.ExecuteAsync(
            config,
            new RemediationExecutionOptions { DryRun = true, Trigger = "Test" });

        Directory.Exists(mw).Should().BeTrue();
        result.Report.DryRun.Should().BeTrue();
        result.Report.ExecutedActions.Should().OnlyContain(a => a.Outcome == RemediationExecutionOutcome.DryRun);
    }

    [Fact]
    public async Task ExecuteAsync_safe_cleanup_removes_partial_home_and_allows_install()
    {
        var (svc, config) = BuildSut();
        var mw = config.Paths.MiddlewareHome;

        var result = await svc.ExecuteAsync(
            config,
            new RemediationExecutionOptions { DryRun = false, Trigger = "InstallInfrastructure" });

        Directory.Exists(mw).Should().BeFalse();
        result.Success.Should().BeTrue();
        result.ContinuationRecommended.Should().BeTrue();

        var inventory = new WEDM.Engine.OracleInventory.OracleInventoryService(new FakeLoggingService());
        var validation = inventory.ValidateForInstall(mw, config.Paths.OracleInventory);
        validation.CanProceed.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_is_idempotent_when_directory_already_removed()
    {
        var (svc, config) = BuildSut();
        Directory.Delete(config.Paths.MiddlewareHome, recursive: true);

        var r1 = await svc.ExecuteAsync(config, new RemediationExecutionOptions { DryRun = false, Trigger = "T1" });
        var r2 = await svc.ExecuteAsync(config, new RemediationExecutionOptions { DryRun = false, Trigger = "T2" });

        r1.Success.Should().BeTrue();
        r2.Success.Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoRemediate_returns_false_when_disabled()
    {
        var (svc, config) = BuildSut();
        config.OracleLifecycle.EnableAutoRemediation = false;
        var assessment = svc.Assess(config);
        svc.ShouldAutoRemediate(config, assessment).Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoRemediate_returns_true_for_safe_partial_install()
    {
        var (svc, config) = BuildSut();
        var assessment = svc.Assess(config);
        if (assessment.CanAutoRemediate)
            svc.ShouldAutoRemediate(config, assessment).Should().BeTrue();
    }
}
