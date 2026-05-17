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

public sealed class InstallRemediationOrchestratorTests : IDisposable
{
    private readonly string _root;
    private bool _disposed;

    public InstallRemediationOrchestratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"WEDM_InstRem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private (InstallRemediationOrchestrator orchestrator, WEDM.Engine.OracleInventory.OracleInventoryService inventory, DeploymentConfiguration config) BuildSut(
        bool partialMarker = true,
        bool autoEnabled = true)
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
        var remediation = new OracleRemediationService(classifier, planBuilder, engine, log);
        var orchestrator = new InstallRemediationOrchestrator(remediation, log);

        var inv = Path.Combine(_root, "oraInventory");
        Directory.CreateDirectory(Path.Combine(inv, "ContentsXML"));
        File.WriteAllText(Path.Combine(inv, "ContentsXML", "inventory.xml"),
            """<?xml version="1.0"?><INVENTORY><HOME_LIST/></INVENTORY>""");

        var mw = Path.Combine(_root, "middleware");
        Directory.CreateDirectory(mw);
        if (partialMarker)
            Directory.CreateDirectory(Path.Combine(mw, "inventory"));

        var config = new DeploymentConfiguration
        {
            Paths = new PathConfiguration
            {
                MiddlewareHome   = mw,
                OracleInventory  = inv,
                TempDirectory    = Path.Combine(_root, "temp"),
                ReportsDirectory = Path.Combine(_root, "reports"),
            },
            OracleLifecycle = new OracleLifecycleConfiguration
            {
                EnableAutoRemediation        = autoEnabled,
                AutoRemediationMode          = AutoRemediationMode.AutomaticSafeOnly,
                SafeCleanupOnly              = true,
                AutoContinueAfterRemediation = true,
                MaxRemediationAttempts       = 2,
            },
        };
        Directory.CreateDirectory(config.Paths.TempDirectory);
        Directory.CreateDirectory(config.Paths.ReportsDirectory);

        return (orchestrator, inventory, config);
    }

    [Fact]
    public async Task EnsureInstallReady_auto_cleans_partial_home_then_allows_install()
    {
        var (orchestrator, inventory, config) = BuildSut();
        var mw = config.Paths.MiddlewareHome;

        var gate = await orchestrator.EnsureInstallReadyAsync(
            config, "InstallInfrastructure", 1, inventory);

        Directory.Exists(mw).Should().BeFalse();
        gate.CanProceedToInstall.Should().BeTrue();
        gate.RemediationExecuted.Should().BeTrue();
        gate.Phase.Should().Be(OracleRemediationPhase.VerifiedClean);
    }

    [Fact]
    public async Task EnsureInstallReady_dry_run_does_not_delete_directory()
    {
        var (orchestrator, inventory, config) = BuildSut();
        var mw = config.Paths.MiddlewareHome;
        var remediation = new OracleRemediationService(
            new PartialInstallClassifier(inventory, new WEDM.Engine.Decommissioning.OracleInventoryService(),
                new OracleHomeSafetyAnalyzer(new OracleProcessManager(), inventory)),
            new RemediationPlanBuilder(),
            new RemediationExecutionEngine(
                new OraclePartialInstallRemediator(new FakeLoggingService(),
                    new WEDM.Engine.Decommissioning.OracleInventoryService(), new OracleProcessManager()),
                new RemediationVerificationService(inventory, new OracleProcessManager()),
                new RemediationReportBuilder(),
                new FakeLoggingService()),
            new FakeLoggingService());

        var assessment = remediation.Assess(config);
        var dryResult = await remediation.ExecuteAsync(
            config,
            new RemediationExecutionOptions { DryRun = true, Trigger = "Test" });

        Directory.Exists(mw).Should().BeTrue();
        dryResult.Report.DryRun.Should().BeTrue();
        dryResult.Report.ExecutedActions.Should().OnlyContain(a => a.Outcome == RemediationExecutionOutcome.DryRun);
    }

    [Fact]
    public async Task EnsureInstallReady_blocks_when_auto_remediation_disabled()
    {
        var (orchestrator, inventory, config) = BuildSut(autoEnabled: false);

        var gate = await orchestrator.EnsureInstallReadyAsync(
            config, "InstallInfrastructure", 1, inventory);

        gate.CanProceedToInstall.Should().BeFalse();
        gate.Phase.Should().Be(OracleRemediationPhase.Skipped);
        Directory.Exists(config.Paths.MiddlewareHome).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureInstallReady_executes_remediation_only_once_per_attempt()
    {
        var (orchestrator, inventory, config) = BuildSut();

        var gate1 = await orchestrator.EnsureInstallReadyAsync(
            config, "InstallInfrastructure", 1, inventory);
        var gate2 = await orchestrator.EnsureInstallReadyAsync(
            config, "InstallInfrastructure", 1, inventory);

        gate1.RemediationExecuted.Should().BeTrue();
        gate2.RemediationExecuted.Should().BeFalse("second call re-validates only");
        gate2.CanProceedToInstall.Should().BeTrue();
        config.RemediationSession.TotalExecutions.Should().Be(1);
        config.RemediationSession.ExecutedForStepAttempt.Should().Contain("InstallInfrastructure:a1");
    }

    [Fact]
    public async Task EnsureInstallReady_respects_max_remediation_attempts()
    {
        var (orchestrator, inventory, config) = BuildSut(partialMarker: true);
        config.OracleLifecycle.MaxRemediationAttempts = 0;

        var gate = await orchestrator.EnsureInstallReadyAsync(
            config, "InstallInfrastructure", 1, inventory);

        gate.CanProceedToInstall.Should().BeFalse();
        gate.Phase.Should().Be(OracleRemediationPhase.Failed);
    }

    [Fact]
    public void SafetyAnalyzer_does_not_block_on_unrelated_oracle_service()
    {
        var log       = new FakeLoggingService();
        var inventory = new WEDM.Engine.OracleInventory.OracleInventoryService(log);
        var safety    = new OracleHomeSafetyAnalyzer(new OracleProcessManager(), inventory);
        var ctx = new RemediationDiscoveryContext
        {
            MiddlewareHome      = Path.Combine(_root, "mw-svc"),
            OracleInventoryPath = Path.Combine(_root, "inv-svc"),
        };

        var result = safety.Analyze(ctx, OracleRemediationState.PartialInstall);
        result.BlockingReasons.Should().NotContain(r => r.Contains("Windows service", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SafetyAnalyzer_does_not_false_positive_on_middleware_inventory_folder()
    {
        var log       = new FakeLoggingService();
        var inventory = new WEDM.Engine.OracleInventory.OracleInventoryService(log);
        var safety    = new OracleHomeSafetyAnalyzer(new OracleProcessManager(), inventory);
        var mw = Path.Combine(_root, "mw-inv");
        Directory.CreateDirectory(Path.Combine(mw, "inventory"));
        File.WriteAllText(Path.Combine(mw, "inventory", "readme.txt"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(mw, "inventory", "readme.txt"), DateTime.UtcNow.AddDays(-30));

        var ctx = new RemediationDiscoveryContext
        {
            MiddlewareHome      = mw,
            OracleInventoryPath = Path.Combine(_root, "inv2"),
            StaleInstallActivityMinutes = 15,
        };

        var result = safety.Analyze(ctx, OracleRemediationState.PartialInstall);
        result.BlockingReasons.Should().NotContain(r =>
            r.Contains("Recent installer activity", StringComparison.OrdinalIgnoreCase));
    }
}
