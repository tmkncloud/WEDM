using FluentAssertions;
using Moq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Tests.Fakes;
using WEDM.Engine.Workflow;
using Xunit;

namespace WEDM.Engine.Tests.Workflow;

/// <summary>
/// Integration tests for <see cref="DeploymentWorkflowEngine.RollbackAsync"/>.
///
/// Each test exercises the rollback path in isolation using FakeStepExecutorFactory
/// and a minimal DeploymentConfiguration.  The tests cover all outcome branches
/// documented in the RollbackSummary audit: RolledBack, NoExecutor, Failed, Exception.
/// </summary>
public sealed class RollbackSummaryIntegrationTests
{
    // ── Shared helpers ────────────────────────────────────────────────────────

    private static DeploymentConfiguration MinimalConfig() => new()
    {
        Name           = "Test Deployment",
        WebLogicVersion = WebLogicVersion.WLS_12c,
        Components     = InstallationComponents.None,
    };

    /// <summary>
    /// Creates a Succeeded step that is eligible for rollback.
    /// </summary>
    private static DeploymentStep SucceededStep(int sequence, string name, string rollbackAction) =>
        new()
        {
            Sequence       = sequence,
            Name           = name,
            Description    = name,
            Category       = "Test",
            IsRequired     = true,
            CanRollback    = true,
            RollbackAction = rollbackAction,
            Status         = StepStatus.Succeeded,
        };

    private static DeploymentWorkflowEngine BuildEngine(
        FakeStepExecutorFactory factory,
        FakeLoggingService? log = null)
    {
        log ??= new FakeLoggingService();
        var planAccessor = new Mock<IDeploymentPlanAccessor>();
        planAccessor.Setup(a => a.Bind(It.IsAny<IReadOnlyList<DeploymentStep>>()));
        planAccessor.Setup(a => a.Clear());
        return new DeploymentWorkflowEngine(log, factory, planAccessor.Object);
    }

    // ── Test 1 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_AllExecutorsSucceed_ReturnsFullyRolledBack()
    {
        var steps = new List<DeploymentStep>
        {
            SucceededStep(1, "CreateOracleFolders",   "Remove-OracleFolders"),
            SucceededStep(2, "InstallJDK",            "Remove-JDK"),
            SucceededStep(3, "InstallWebLogic",       "Remove-MiddlewareHome"),
        };

        var rollbackExecutors = new Dictionary<string, WEDM.Engine.Workflow.Steps.IStepExecutor>
        {
            ["Remove-OracleFolders"]  = new FakeStepExecutor(succeed: true),
            ["Remove-JDK"]            = new FakeStepExecutor(succeed: true),
            ["Remove-MiddlewareHome"] = new FakeStepExecutor(succeed: true),
        };

        var factory = new FakeStepExecutorFactory(rollbackExecutors: rollbackExecutors);
        var engine  = BuildEngine(factory);

        var summary = await engine.RollbackAsync(steps, MinimalConfig());

        summary.FullyRolledBack.Should().BeTrue();
        summary.StepsRolledBack.Should().Be(3);
        summary.StepsFailed.Should().Be(0);
        summary.StepsNoExecutor.Should().Be(0);
        summary.Records.Should().HaveCount(3);
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_NoExecutorRegistered_ReportsStepsNoExecutor()
    {
        var steps = new List<DeploymentStep>
        {
            SucceededStep(1, "InstallJDK",      "Remove-JDK"),
            SucceededStep(2, "InstallWebLogic", "Remove-MiddlewareHome"),
        };

        // Empty rollback executor registry — nothing is registered
        var factory = new FakeStepExecutorFactory();
        var engine  = BuildEngine(factory);

        var summary = await engine.RollbackAsync(steps, MinimalConfig());

        summary.FullyRolledBack.Should().BeFalse();
        summary.StepsNoExecutor.Should().Be(2);
        summary.StepsRolledBack.Should().Be(0);
        summary.StepsFailed.Should().Be(0);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_ExecutorReturnsFailure_ReportsStepsFailed()
    {
        var steps = new List<DeploymentStep>
        {
            SucceededStep(1, "InstallJDK", "Remove-JDK"),
        };

        var rollbackExecutors = new Dictionary<string, WEDM.Engine.Workflow.Steps.IStepExecutor>
        {
            ["Remove-JDK"] = new FakeStepExecutor(succeed: false, error: "Cannot remove locked JDK directory"),
        };

        var factory = new FakeStepExecutorFactory(rollbackExecutors: rollbackExecutors);
        var engine  = BuildEngine(factory);

        var summary = await engine.RollbackAsync(steps, MinimalConfig());

        summary.StepsFailed.Should().Be(1);
        summary.FullyRolledBack.Should().BeFalse();
        steps[0].Status.Should().Be(StepStatus.RollbackFailed);
        summary.Records[0].Outcome.Should().Be("Failed");
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_ExecutorThrows_CapturesExceptionInRecord()
    {
        var steps = new List<DeploymentStep>
        {
            SucceededStep(1, "CreateDomain", "Remove-Domain"),
        };

        var rollbackExecutors = new Dictionary<string, WEDM.Engine.Workflow.Steps.IStepExecutor>
        {
            ["Remove-Domain"] = new ThrowingStepExecutor(new IOException("Disk I/O error during rollback")),
        };

        var factory = new FakeStepExecutorFactory(rollbackExecutors: rollbackExecutors);
        var engine  = BuildEngine(factory);

        // Must NOT throw — rollback catches all exceptions internally
        var summary = await engine.RollbackAsync(steps, MinimalConfig());

        summary.Records.Should().HaveCount(1);
        summary.Records[0].Outcome.Should().Be("Exception");
        summary.Records[0].Error.Should().Contain("Disk I/O error during rollback");
        summary.Records[0].Success.Should().BeFalse();
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_MixedOutcomes_TracksAllThree()
    {
        var steps = new List<DeploymentStep>
        {
            SucceededStep(1, "CreateOracleFolders", "Remove-OracleFolders"),   // success
            SucceededStep(2, "InstallJDK",          "Remove-JDK"),             // no executor
            SucceededStep(3, "InstallWebLogic",     "Remove-MiddlewareHome"),  // failure
        };

        var rollbackExecutors = new Dictionary<string, WEDM.Engine.Workflow.Steps.IStepExecutor>
        {
            ["Remove-OracleFolders"]  = new FakeStepExecutor(succeed: true),
            // Remove-JDK intentionally absent → NoExecutor
            ["Remove-MiddlewareHome"] = new FakeStepExecutor(succeed: false, error: "Access denied"),
        };

        var factory = new FakeStepExecutorFactory(rollbackExecutors: rollbackExecutors);
        var engine  = BuildEngine(factory);

        var summary = await engine.RollbackAsync(steps, MinimalConfig());

        summary.FullyRolledBack.Should().BeFalse();
        summary.StepsRolledBack.Should().Be(1);
        summary.StepsNoExecutor.Should().Be(1);
        summary.StepsFailed.Should().Be(1);
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_StepsInReverseOrder()
    {
        var executionOrder = new List<int>();

        // Create steps with sequences 1, 3, 5 in non-sequential order
        var steps = new List<DeploymentStep>
        {
            SucceededStep(3, "Step3", "Rollback-Step3"),
            SucceededStep(1, "Step1", "Rollback-Step1"),
            SucceededStep(5, "Step5", "Rollback-Step5"),
        };

        // Recording executor that captures the sequence number of the step it processes
        var recordingExecutors = new Dictionary<string, WEDM.Engine.Workflow.Steps.IStepExecutor>
        {
            ["Rollback-Step1"] = new RecordingExecutor(executionOrder, 1),
            ["Rollback-Step3"] = new RecordingExecutor(executionOrder, 3),
            ["Rollback-Step5"] = new RecordingExecutor(executionOrder, 5),
        };

        var factory = new FakeStepExecutorFactory(rollbackExecutors: recordingExecutors);
        var engine  = BuildEngine(factory);

        await engine.RollbackAsync(steps, MinimalConfig());

        // Must be reversed: 5, 3, 1
        executionOrder.Should().Equal([5, 3, 1]);
    }

    // ── Test 7 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_OnlySucceededStepsEligible()
    {
        var steps = new List<DeploymentStep>
        {
            SucceededStep(1, "InstallJDK",      "Remove-JDK"),
            new DeploymentStep
            {
                Sequence       = 2,
                Name           = "InstallWebLogic",
                Description    = "Install WLS",
                Category       = "Test",
                IsRequired     = true,
                CanRollback    = true,
                RollbackAction = "Remove-MiddlewareHome",
                Status         = StepStatus.Failed,   // <-- failed, must NOT be rolled back
            },
        };

        var rollbackExecutors = new Dictionary<string, WEDM.Engine.Workflow.Steps.IStepExecutor>
        {
            ["Remove-JDK"]            = new FakeStepExecutor(succeed: true),
            ["Remove-MiddlewareHome"] = new FakeStepExecutor(succeed: true),
        };

        var factory = new FakeStepExecutorFactory(rollbackExecutors: rollbackExecutors);
        var engine  = BuildEngine(factory);

        var summary = await engine.RollbackAsync(steps, MinimalConfig());

        // Only 1 step (the Succeeded one) should be in the rollback records
        summary.Records.Should().HaveCount(1);
        summary.Records[0].StepName.Should().Be("InstallJDK");
        summary.StepsRolledBack.Should().Be(1);
    }

    // ── Test 8 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_CanRollbackFalse_StepExcluded()
    {
        var steps = new List<DeploymentStep>
        {
            SucceededStep(1, "InstallJDK", "Remove-JDK"),
            new DeploymentStep
            {
                Sequence       = 2,
                Name           = "CreateSnapshot",
                Description    = "Take snapshot",
                Category       = "Test",
                IsRequired     = true,
                CanRollback    = false,               // <-- explicitly non-rollbackable
                RollbackAction = "Remove-Snapshot",
                Status         = StepStatus.Succeeded,
            },
        };

        var rollbackExecutors = new Dictionary<string, WEDM.Engine.Workflow.Steps.IStepExecutor>
        {
            ["Remove-JDK"]      = new FakeStepExecutor(succeed: true),
            ["Remove-Snapshot"] = new FakeStepExecutor(succeed: true),
        };

        var factory = new FakeStepExecutorFactory(rollbackExecutors: rollbackExecutors);
        var engine  = BuildEngine(factory);

        var summary = await engine.RollbackAsync(steps, MinimalConfig());

        // Only the CanRollback=true step should appear
        summary.Records.Should().HaveCount(1);
        summary.Records[0].StepName.Should().Be("InstallJDK");
    }

    // ── Test 9 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_RecordsDuration()
    {
        var steps = new List<DeploymentStep>
        {
            SucceededStep(1, "InstallJDK", "Remove-JDK"),
        };

        var rollbackExecutors = new Dictionary<string, WEDM.Engine.Workflow.Steps.IStepExecutor>
        {
            // delay: true causes a 1 ms await before returning, so Duration > Zero
            ["Remove-JDK"] = new FakeStepExecutor(succeed: true, delay: true),
        };

        var factory = new FakeStepExecutorFactory(rollbackExecutors: rollbackExecutors);
        var engine  = BuildEngine(factory);

        var summary = await engine.RollbackAsync(steps, MinimalConfig());

        summary.Records.Should().HaveCount(1);
        summary.Records[0].Duration.Should().BeGreaterThan(TimeSpan.Zero,
            because: "the stopwatch measures real elapsed time even for fast operations");
    }

    // ── Test 10 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackAsync_StepStatusUpdated_ToRolledBack()
    {
        var step = SucceededStep(1, "CreateDomain", "Remove-Domain");
        var steps = new List<DeploymentStep> { step };

        var rollbackExecutors = new Dictionary<string, WEDM.Engine.Workflow.Steps.IStepExecutor>
        {
            ["Remove-Domain"] = new FakeStepExecutor(succeed: true),
        };

        var factory = new FakeStepExecutorFactory(rollbackExecutors: rollbackExecutors);
        var engine  = BuildEngine(factory);

        await engine.RollbackAsync(steps, MinimalConfig());

        step.Status.Should().Be(StepStatus.RolledBack,
            because: "a successful rollback must transition the step to RolledBack status");
    }

    // ── Helper executor for order-tracking (Test 6) ───────────────────────────

    private sealed class RecordingExecutor : WEDM.Engine.Workflow.Steps.IStepExecutor
    {
        private readonly List<int> _log;
        private readonly int       _sequence;

        public RecordingExecutor(List<int> log, int sequence)
        {
            _log      = log;
            _sequence = sequence;
        }

        public Task<StepExecutionResult> ExecuteAsync(
            DeploymentStep step,
            DeploymentConfiguration config,
            CancellationToken cancellationToken = default)
        {
            _log.Add(_sequence);
            return Task.FromResult(StepExecutionResult.Ok("recorded"));
        }
    }
}
