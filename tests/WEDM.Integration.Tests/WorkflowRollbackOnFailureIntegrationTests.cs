using Moq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Workflow;
using WEDM.Engine.Workflow.Steps;
using FluentAssertions;
using Xunit;

namespace Orchestration.Integration.Tests;

public sealed class WorkflowRollbackOnFailureIntegrationTests
{
    private sealed class OkExecutor : IStepExecutor
    {
        public Task<StepExecutionResult> ExecuteAsync(
            DeploymentStep step,
            DeploymentConfiguration config,
            CancellationToken cancellationToken = default)
            => Task.FromResult(StepExecutionResult.Ok());
    }

    private sealed class FailExecutor : IStepExecutor
    {
        public Task<StepExecutionResult> ExecuteAsync(
            DeploymentStep step,
            DeploymentConfiguration config,
            CancellationToken cancellationToken = default)
            => Task.FromResult(StepExecutionResult.Fail("boom"));
    }

    private sealed class CountingRollback : IStepExecutor
    {
        public int Hits;
        public Task<StepExecutionResult> ExecuteAsync(
            DeploymentStep step,
            DeploymentConfiguration config,
            CancellationToken cancellationToken = default)
        {
            Hits++;
            return Task.FromResult(StepExecutionResult.Ok());
        }
    }

    [Fact]
    public async Task RunAsync_PreviousSucceededStep_RunsRegisteredRollbackExecutor()
    {
        var logMock        = new Mock<ILoggingService>();
        var planAccessor   = new Mock<IDeploymentPlanAccessor>();
        planAccessor.Setup(a => a.Bind(It.IsAny<IReadOnlyList<DeploymentStep>>()));
        planAccessor.Setup(a => a.Clear());

        var countingRb = new CountingRollback();
        var fwd = new Dictionary<string, IStepExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["First"]          = new OkExecutor(),
            ["BreakOnPurpose"] = new FailExecutor(),
        };
        var rb = new Dictionary<string, IStepExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["Remove-OracleFolders"] = countingRb,
        };

        var factory = new StepExecutorFactory(fwd, rb, _ => null, _ => null);
        var engine  = new DeploymentWorkflowEngine(logMock.Object, factory, planAccessor.Object);

        var steps = new List<DeploymentStep>
        {
            new()
            {
                Sequence       = 1,
                Name           = "First",
                Description    = "n/a",
                Category       = "Z",
                IsRequired     = true,
                CanRollback    = true,
                RollbackAction = "Remove-OracleFolders",
            },
            new()
            {
                Sequence    = 2,
                Name        = "BreakOnPurpose",
                Description = "n/a",
                Category    = "Z",
                IsRequired  = true,
                CanRollback = false,
                MaxRetries  = 0,
            },
        };

        var cfg = new DeploymentConfiguration
        {
            Name = "Rollback integration",
            Paths = new PathConfiguration(),
            EnableRollback = true,
        };

        var report = await engine.RunAsync(cfg, steps, CancellationToken.None);

        report.FinalStatus.Should().Be(DeploymentStatus.RolledBack);
        report.Rollback.Should().NotBeNull();
        report.Rollback!.StepsRolledBack.Should().Be(1);
        countingRb.Hits.Should().Be(1);
    }
}
