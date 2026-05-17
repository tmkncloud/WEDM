using System.Text.Json;
using FluentAssertions;
using Moq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Workflow;
using WEDM.Engine.Workflow.Steps;
using WEDM.Infrastructure.Deployment;
using Xunit;

namespace Orchestration.Integration.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// RollbackCompensationPersistenceTests
// ═══════════════════════════════════════════════════════════════════════════════
//
// Covers R-01: OracleRollbackCompensation must survive checkpoint/crash/resume.
//
// Test scenarios:
//   1. Compensation serialization roundtrip (snapshot → JSON → restore)
//   2. Null compensation roundtrips as null (non-Oracle steps)
//   3. Old checkpoint JSON without rollbackCompensation field still loads cleanly
//   4. Session store persists and restores compensation through full checkpoint cycle
//   5. ApplyResumeState restores compensation with CompensationSource.Restored
//   6. Retry-isolated paths survive checkpoint roundtrip
//   7. RollbackAsync sets CompensationSource on RollbackStepRecord diagnostics
//   8. Rollback after crash uses restored compensation, not config fallback
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RollbackCompensationPersistenceTests : IDisposable
{
    private readonly string _root;
    private readonly JsonDeploymentSessionStore _store;

    public RollbackCompensationPersistenceTests()
    {
        _root  = Path.Combine(Path.GetTempPath(), "wedm-comp-tests", Guid.NewGuid().ToString("N"));
        _store = new JsonDeploymentSessionStore(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OracleRollbackCompensation MakeCompensation(string home = @"C:\Oracle\MW") =>
        new()
        {
            OracleHomePaths             = [home],
            OracleInventoryPath         = @"C:\Oracle\Inventory",
            CreatedServiceNames         = ["WLS_AdminServer"],
            SetEnvironmentVariableNames = ["JAVA_HOME"],
            CreatedRegistryKeyPaths     = [@"SOFTWARE\ORACLE\KEY_TEST"],
            GeneratedFilePaths          = [@"C:\Temp\wedm-session\response.rsp"],
            Source                      = CompensationSource.Runtime,
            CapturedAt                  = new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero),
        };

    private static DeploymentStep MakeSucceededStep(
        string name = "InstallWebLogic",
        OracleRollbackCompensation? comp = null) =>
        new()
        {
            Name                 = name,
            Sequence             = 1,
            Status               = StepStatus.Succeeded,
            RollbackAction       = "Remove-MiddlewareHome",
            CanRollback          = true,
            RollbackCompensation = comp,
        };

    // ─────────────────────────────────────────────────────────────────────────
    // 1. Compensation serialization roundtrip
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_roundtrips_compensation_through_json()
    {
        // Arrange
        var comp = MakeCompensation();
        var step = MakeSucceededStep(comp: comp);

        // Act — snapshot → JSON → deserialize → DeploymentStep
        var snapshot   = DeploymentStepSnapshot.FromStep(step);
        var json       = JsonSerializer.Serialize(snapshot, DeploymentJsonOptions.Create());
        var restored   = JsonSerializer.Deserialize<DeploymentStepSnapshot>(json, DeploymentJsonOptions.Create())!;
        var stepBack   = restored.ToDeploymentStep();

        // Assert — all fields preserved
        stepBack.RollbackCompensation.Should().NotBeNull();
        stepBack.RollbackCompensation!.OracleHomePaths.Should().Equal(@"C:\Oracle\MW");
        stepBack.RollbackCompensation.OracleInventoryPath.Should().Be(@"C:\Oracle\Inventory");
        stepBack.RollbackCompensation.CreatedServiceNames.Should().Equal("WLS_AdminServer");
        stepBack.RollbackCompensation.SetEnvironmentVariableNames.Should().Equal("JAVA_HOME");
        stepBack.RollbackCompensation.CreatedRegistryKeyPaths.Should().Equal(@"SOFTWARE\ORACLE\KEY_TEST");
        stepBack.RollbackCompensation.GeneratedFilePaths.Should().Equal(@"C:\Temp\wedm-session\response.rsp");
        stepBack.RollbackCompensation.CapturedAt.Should().Be(comp.CapturedAt);
    }

    [Fact]
    public void Snapshot_roundtrip_changes_source_from_Runtime_to_Restored()
    {
        // Runtime at capture time; Restored after deserialization
        var snapshot  = DeploymentStepSnapshot.FromStep(MakeSucceededStep(comp: MakeCompensation()));
        var json      = JsonSerializer.Serialize(snapshot, DeploymentJsonOptions.Create());
        var restored  = JsonSerializer.Deserialize<DeploymentStepSnapshot>(json, DeploymentJsonOptions.Create())!;

        // The snapshot itself preserves Runtime (raw JSON)
        restored.RollbackCompensation!.Source.Should().Be(CompensationSource.Runtime,
            "snapshot JSON preserves the source as-is; ToDeploymentStep() performs the transition");

        // ToDeploymentStep() transitions Runtime → Restored
        var stepBack = restored.ToDeploymentStep();
        stepBack.RollbackCompensation!.Source.Should().Be(CompensationSource.Restored,
            "ToDeploymentStep() must tag checkpoint-restored compensation as Restored");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. Null compensation roundtrips as null (non-Oracle steps)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_null_compensation_roundtrips_as_null()
    {
        var step     = MakeSucceededStep("InstallJDK", comp: null);
        var snapshot = DeploymentStepSnapshot.FromStep(step);
        var json     = JsonSerializer.Serialize(snapshot, DeploymentJsonOptions.Create());
        var restored = JsonSerializer.Deserialize<DeploymentStepSnapshot>(json, DeploymentJsonOptions.Create())!;
        var stepBack = restored.ToDeploymentStep();

        stepBack.RollbackCompensation.Should().BeNull(
            "non-Oracle steps carry no compensation — null must round-trip as null");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. Old checkpoint JSON without rollbackCompensation field still loads
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Old_checkpoint_without_compensation_field_deserializes_without_error()
    {
        // Simulate a pre-R01-fix checkpoint that has no rollbackCompensation property
        const string oldJson = """
            {
              "id": "00000000-0000-0000-0000-000000000001",
              "sequence": 1,
              "name": "InstallWebLogic",
              "description": "",
              "category": "WebLogic",
              "status": 2,
              "attemptCount": 1,
              "maxRetries": 2,
              "isRequired": true,
              "canRollback": true,
              "rollbackAction": "Remove-MiddlewareHome",
              "outputLog": "OUI exited 0",
              "errorMessage": "",
              "exitCode": 0,
              "progressPercent": 100.0
            }
            """;

        var snapshot = JsonSerializer.Deserialize<DeploymentStepSnapshot>(
            oldJson, DeploymentJsonOptions.Create());

        snapshot.Should().NotBeNull();
        snapshot!.RollbackCompensation.Should().BeNull(
            "old checkpoints have no compensation — rollback falls back to config paths");
        snapshot.Name.Should().Be("InstallWebLogic");
        snapshot.Status.Should().Be(StepStatus.Succeeded);

        // ToDeploymentStep must succeed with null compensation
        var step = snapshot.ToDeploymentStep();
        step.RollbackCompensation.Should().BeNull();
        step.Name.Should().Be("InstallWebLogic");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. Session store persists compensation through full checkpoint cycle
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SessionStore_roundtrips_compensation_through_full_checkpoint()
    {
        var comp      = MakeCompensation();
        var sessionId = Guid.NewGuid();
        var state = new DeploymentSessionState
        {
            SessionId       = sessionId,
            LifecycleStatus = DeploymentLifecycleStatus.InProgress,
            Configuration   = new DeploymentConfiguration { Name = "comp-persist-test" },
            Steps           =
            [
                DeploymentStepSnapshot.FromStep(MakeSucceededStep(comp: comp))
            ],
        };

        // Save and reload via JsonDeploymentSessionStore (uses AtomicFileWriter)
        await _store.SaveAsync(state);
        var loaded = await _store.LoadAsync(sessionId);

        loaded.Should().NotBeNull();
        loaded!.Steps.Should().ContainSingle();

        var snap = loaded.Steps[0];
        snap.RollbackCompensation.Should().NotBeNull("compensation must survive save/load");
        snap.RollbackCompensation!.OracleHomePaths.Should().Equal(@"C:\Oracle\MW");
        snap.RollbackCompensation.CreatedServiceNames.Should().Equal("WLS_AdminServer");
        // Source stored as Runtime in the JSON snapshot
        snap.RollbackCompensation.Source.Should().Be(CompensationSource.Runtime);

        // ToDeploymentStep transitions Runtime → Restored
        var stepBack = snap.ToDeploymentStep();
        stepBack.RollbackCompensation!.Source.Should().Be(CompensationSource.Restored);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5. ApplyResumeState restores compensation with Restored source
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_restores_compensation_from_checkpoint_on_resume()
    {
        // Simulate: step 1 (InstallWebLogic) succeeded and was checkpointed with compensation
        // captured at retry-isolation paths.  Step 2 (StepB) never ran.
        // After a crash, step 2 should fail, triggering rollback of step 1.
        // The rollback executor must receive the CHECKPOINT paths, not the config paths.

        var checkpointHome = @"C:\Oracle\MW_RetryIsolated";  // actual install path
        var configHome     = @"C:\Oracle\MW_Config";          // different path in config

        var comp = new OracleRollbackCompensation
        {
            OracleHomePaths     = [checkpointHome],
            OracleInventoryPath = @"C:\Oracle\Inventory",
            Source              = CompensationSource.Runtime,  // stored as Runtime in checkpoint JSON
        };

        var resume = new DeploymentSessionState
        {
            SessionId = Guid.NewGuid(),
            Steps     =
            [
                DeploymentStepSnapshot.FromStep(new DeploymentStep
                {
                    Name                 = "InstallWebLogic",
                    Sequence             = 1,
                    Status               = StepStatus.Succeeded,
                    RollbackAction       = "Remove-MiddlewareHome",
                    CanRollback          = true,
                    RollbackCompensation = comp,
                }),
            ],
        };

        // Capture what compensation the rollback executor sees
        OracleRollbackCompensation? capturedCompensation = null;
        var rollbackExecutor = new CapturingRollbackExecutor(
            onExecute: s => capturedCompensation = s.RollbackCompensation);

        var log     = new Mock<ILoggingService>();
        var factory = new Mock<IStepExecutorFactory>();
        var plan    = new Mock<IDeploymentPlanAccessor>();

        factory.Setup(f => f.GetExecutor("StepB"))
               .Returns(new AlwaysFailExecutor());
        factory.Setup(f => f.GetRollbackExecutor("Remove-MiddlewareHome"))
               .Returns(rollbackExecutor);

        var engine = new DeploymentWorkflowEngine(log.Object, factory.Object, plan.Object);
        var config = new DeploymentConfiguration
        {
            Name          = "resume-compensation-test",
            EnableRollback = true,
            Paths         = new PathConfiguration { MiddlewareHome = configHome },
        };

        var steps = new List<DeploymentStep>
        {
            new()
            {
                Sequence      = 1,
                Name          = "InstallWebLogic",
                IsRequired    = true,
                MaxRetries    = 0,
                CanRollback   = true,
                RollbackAction = "Remove-MiddlewareHome",
            },
            new()
            {
                Sequence   = 2,
                Name       = "StepB",
                IsRequired = true,
                MaxRetries = 0,
                CanRollback = false,
            },
        };

        await engine.RunAsync(
            config,
            steps,
            new DeploymentWorkflowRunContext
            {
                SessionId   = resume.SessionId,
                ResumeState = resume,
            },
            CancellationToken.None);

        // The rollback executor must have received the checkpoint paths
        capturedCompensation.Should().NotBeNull(
            "compensation must be restored from checkpoint and passed to rollback executor");
        capturedCompensation!.OracleHomePaths.Should().Equal(checkpointHome,
            "rollback must use checkpoint paths, NOT config paths");
        capturedCompensation.OracleHomePaths.Should().NotContain(configHome,
            "config path must not be used when checkpoint compensation is available");
        capturedCompensation.Source.Should().Be(CompensationSource.Restored,
            "ToDeploymentStep() must have tagged the compensation as Restored");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 6. Retry-isolated paths survive checkpoint roundtrip
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Retry_isolated_compensation_paths_survive_full_roundtrip()
    {
        // When retry isolation is active, TempDirectory changes per attempt.
        // The compensation captures the actual install-time paths.
        // After a crash, the original isolated paths must be preserved.

        var isolatedHome    = @"C:\Oracle\MW_Retry_Isolated";
        var isolatedTempRsp = $@"C:\Temp\wedm-retry-installweblogic-a2-{Guid.NewGuid():N}\response.rsp";

        var comp = new OracleRollbackCompensation
        {
            OracleHomePaths     = [isolatedHome],
            OracleInventoryPath = @"C:\Oracle\Inventory",
            GeneratedFilePaths  = [isolatedTempRsp],
            Source              = CompensationSource.Runtime,
        };

        var snapshot   = DeploymentStepSnapshot.FromStep(MakeSucceededStep(comp: comp));
        var json       = JsonSerializer.Serialize(snapshot, DeploymentJsonOptions.Create());
        var deserialized = JsonSerializer.Deserialize<DeploymentStepSnapshot>(json, DeploymentJsonOptions.Create())!;
        var stepBack   = deserialized.ToDeploymentStep();

        stepBack.RollbackCompensation!.OracleHomePaths.Should().Equal(isolatedHome,
            "retry-isolated Oracle home path must survive checkpoint roundtrip");
        stepBack.RollbackCompensation.GeneratedFilePaths.Should().Equal(isolatedTempRsp,
            "retry-isolated response file path must survive checkpoint roundtrip");
        stepBack.RollbackCompensation.Source.Should().Be(CompensationSource.Restored);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 7. RollbackAsync sets CompensationSource on RollbackStepRecord
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_sets_CompensationSource_on_record_when_compensation_present()
    {
        var log     = new Mock<ILoggingService>();
        var factory = new Mock<IStepExecutorFactory>();
        var plan    = new Mock<IDeploymentPlanAccessor>();

        // Step with Restored compensation (as if it came from a crash-recovery checkpoint)
        var comp = new OracleRollbackCompensation
        {
            OracleHomePaths = [@"C:\Oracle\MW"],
            Source          = CompensationSource.Restored,
        };

        var succeededStep = new DeploymentStep
        {
            Name                 = "InstallWebLogic",
            Sequence             = 1,
            Status               = StepStatus.Succeeded,
            CanRollback          = true,
            RollbackAction       = "Remove-MiddlewareHome",
            RollbackCompensation = comp,
        };

        factory.Setup(f => f.GetRollbackExecutor("Remove-MiddlewareHome"))
               .Returns(new AlwaysSucceedRollbackExecutor());

        var engine = new DeploymentWorkflowEngine(log.Object, factory.Object, plan.Object);
        var config = new DeploymentConfiguration { EnableRollback = true };

        var summary = await engine.RollbackAsync([succeededStep], config, CancellationToken.None);

        summary.Records.Should().ContainSingle();
        summary.Records[0].CompensationSource.Should().Be(CompensationSource.Restored,
            "RollbackStepRecord must reflect the compensation source from the step");
    }

    [Fact]
    public async Task RollbackAsync_sets_CompensationSource_Fallback_when_no_compensation()
    {
        var log     = new Mock<ILoggingService>();
        var factory = new Mock<IStepExecutorFactory>();
        var plan    = new Mock<IDeploymentPlanAccessor>();

        // Step with null compensation — executor falls back to config paths
        var succeededStep = new DeploymentStep
        {
            Name             = "InstallWebLogic",
            Sequence         = 1,
            Status           = StepStatus.Succeeded,
            CanRollback      = true,
            RollbackAction   = "Remove-MiddlewareHome",
            RollbackCompensation = null,
        };

        factory.Setup(f => f.GetRollbackExecutor("Remove-MiddlewareHome"))
               .Returns(new AlwaysSucceedRollbackExecutor());

        var engine = new DeploymentWorkflowEngine(log.Object, factory.Object, plan.Object);
        var config = new DeploymentConfiguration { EnableRollback = true };

        var summary = await engine.RollbackAsync([succeededStep], config, CancellationToken.None);

        summary.Records[0].CompensationSource.Should().Be(CompensationSource.Fallback,
            "when no compensation is present, record must indicate Fallback");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8. InventorySnapshotBefore (nested object) survives roundtrip
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compensation_nested_InventorySnapshot_survives_roundtrip()
    {
        var comp = new OracleRollbackCompensation
        {
            OracleHomePaths     = [@"C:\Oracle\MW"],
            OracleInventoryPath = @"C:\Oracle\Inventory",
            Source              = CompensationSource.Runtime,
            InventorySnapshotBefore = new OracleInventorySnapshot
            {
                InventoryLoc = @"C:\Oracle\Inventory",
                OracleHomes  =
                [
                    new OracleHomeDescriptor { Path = @"C:\Oracle\MW", Name = "OraHome1" },
                ],
                InventoryHealthy = true,
            },
        };

        var snapshot   = DeploymentStepSnapshot.FromStep(MakeSucceededStep(comp: comp));
        var json       = JsonSerializer.Serialize(snapshot, DeploymentJsonOptions.Create());
        var deserialized = JsonSerializer.Deserialize<DeploymentStepSnapshot>(json, DeploymentJsonOptions.Create())!;
        var stepBack   = deserialized.ToDeploymentStep();

        stepBack.RollbackCompensation!.InventorySnapshotBefore.Should().NotBeNull();
        stepBack.RollbackCompensation.InventorySnapshotBefore!.InventoryLoc.Should().Be(@"C:\Oracle\Inventory");
        stepBack.RollbackCompensation.InventorySnapshotBefore.OracleHomes.Should().ContainSingle();
        stepBack.RollbackCompensation.InventorySnapshotBefore.OracleHomes[0].Path.Should().Be(@"C:\Oracle\MW");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal stubs
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Captures the DeploymentStep the rollback executor received.</summary>
    private sealed class CapturingRollbackExecutor : IStepExecutor
    {
        private readonly Action<DeploymentStep> _onExecute;
        public CapturingRollbackExecutor(Action<DeploymentStep> onExecute) => _onExecute = onExecute;
        public Task<StepExecutionResult> ExecuteAsync(
            DeploymentStep step, DeploymentConfiguration config, CancellationToken ct)
        {
            _onExecute(step);
            return Task.FromResult(StepExecutionResult.Ok("captured"));
        }
    }

    /// <summary>Always returns Fail — forces rollback of earlier succeeded steps.</summary>
    private sealed class AlwaysFailExecutor : IStepExecutor
    {
        public Task<StepExecutionResult> ExecuteAsync(
            DeploymentStep step, DeploymentConfiguration config, CancellationToken ct)
            => Task.FromResult(StepExecutionResult.Fail("forced failure", retryRecommended: false));
    }

    /// <summary>Always returns Ok — used in rollback path tests where we just need the record.</summary>
    private sealed class AlwaysSucceedRollbackExecutor : IStepExecutor
    {
        public Task<StepExecutionResult> ExecuteAsync(
            DeploymentStep step, DeploymentConfiguration config, CancellationToken ct)
            => Task.FromResult(StepExecutionResult.Ok("rolled back"));
    }
}
