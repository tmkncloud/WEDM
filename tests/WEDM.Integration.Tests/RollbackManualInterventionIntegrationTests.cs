using Moq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Workflow;
using WEDM.Engine.Workflow.Steps;
using FluentAssertions;
using Xunit;

namespace Orchestration.Integration.Tests;

public sealed class RollbackManualInterventionIntegrationTests
{
    private static DeploymentConfiguration MinimalConfig() => new()
    {
        Name            = "Test",
        WebLogicVersion = WebLogicVersion.WLS_12c,
        Components      = InstallationComponents.None,
    };

    [Fact]
    public async Task RollbackAsync_ManualIntervention_IncrementsManualCounterNotRolledBack()
    {
        var planAccessor = new Mock<IDeploymentPlanAccessor>();
        planAccessor.Setup(a => a.Bind(It.IsAny<IReadOnlyList<DeploymentStep>>()));
        planAccessor.Setup(a => a.Clear());

        var logMock = new Mock<ILoggingService>();

        var rollback = new Dictionary<string, IStepExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["RollbackOpatchApply"] = new RollbackOpatchApplyStep(logMock.Object),
        };

        var factory = new StepExecutorFactory(
            new Dictionary<string, IStepExecutor>(StringComparer.OrdinalIgnoreCase),
            rollback,
            _ => null,
            _ => null);

        var engine = new DeploymentWorkflowEngine(logMock.Object, factory, planAccessor.Object);

        var steps = new List<DeploymentStep>
        {
            new()
            {
                Sequence       = 1,
                Name           = "OpatchApply",
                Description    = "x",
                Category       = "OPatch",
                Status         = StepStatus.Succeeded,
                CanRollback    = true,
                RollbackAction = "RollbackOpatchApply",
            },
        };

        var summary = await engine.RollbackAsync(steps, MinimalConfig());

        summary.StepsManualInterventionRequired.Should().Be(1);
        summary.StepsRolledBack.Should().Be(0);
        summary.FullyRolledBack.Should().BeFalse();
        summary.Records.Should().ContainSingle(r => r.Outcome == "ManualInterventionRequired");
    }
}
