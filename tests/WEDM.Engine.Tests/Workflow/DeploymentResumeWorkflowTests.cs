using FluentAssertions;
using Moq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Workflow;
using WEDM.Engine.Workflow.Steps;
using Xunit;

namespace WEDM.Engine.Tests.Workflow;

public sealed class DeploymentResumeWorkflowTests
{
    [Fact]
    public async Task RunAsync_skips_steps_already_succeeded_on_resume()
    {
        var log = new Mock<ILoggingService>();
        var factory = new Mock<IStepExecutorFactory>();
        var plan = new Mock<IDeploymentPlanAccessor>();

        var engine = new DeploymentWorkflowEngine(log.Object, factory.Object, plan.Object);
        var config = new DeploymentConfiguration { Name = "resume-test" };

        var steps = new List<DeploymentStep>
        {
            new() { Sequence = 1, Name = "StepA", IsRequired = true, MaxRetries = 0 },
            new() { Sequence = 2, Name = "StepB", IsRequired = true, MaxRetries = 0 }
        };

        var resume = new DeploymentSessionState
        {
            SessionId = Guid.NewGuid(),
            Steps =
            [
                DeploymentStepSnapshot.FromStep(new DeploymentStep
                {
                    Name = "StepA", Sequence = 1, Status = StepStatus.Succeeded
                })
            ]
        };

        var executed = new List<string>();
        factory.Setup(f => f.GetExecutor(It.IsAny<string>()))
            .Returns((string name) => new TrackingExecutor(name, executed));

        var report = await engine.RunAsync(
            config,
            steps,
            new DeploymentWorkflowRunContext { SessionId = resume.SessionId, ResumeState = resume },
            CancellationToken.None);

        executed.Should().ContainSingle().Which.Should().Be("StepB");
        report.FinalStatus.Should().Be(DeploymentStatus.Completed);
    }

    private sealed class TrackingExecutor : IStepExecutor
    {
        private readonly string _name;
        private readonly List<string> _executed;
        public TrackingExecutor(string name, List<string> executed) { _name = name; _executed = executed; }
        public Task<StepExecutionResult> ExecuteAsync(DeploymentStep step, DeploymentConfiguration config, CancellationToken ct)
        {
            _executed.Add(_name);
            return Task.FromResult(StepExecutionResult.Ok("ok"));
        }
    }

    private sealed class StubExecutor : IStepExecutor
    {
        public StubExecutor(string name) => Name = name;
        public string Name { get; }
        public Task<StepExecutionResult> ExecuteAsync(DeploymentStep step, DeploymentConfiguration config, CancellationToken ct)
            => Task.FromResult(StepExecutionResult.Ok("ok"));
    }
}
