using WEDM.Domain.Interfaces;
using WEDM.Engine.Workflow.Steps;
using FluentAssertions;
using Moq;
using Xunit;

namespace Orchestration.Integration.Tests;

/// <summary>
/// Verifies rollback actions declared in deployment steps resolve to concrete executors
/// (parity with DI registration in <c>App.xaml.cs</c> — keep dictionaries in sync when adding steps).
/// </summary>
public sealed class RollbackExecutorRegistrySmokeTests
{
    [Theory]
    [InlineData("Remove-OracleFolders")]
    [InlineData("Remove-JDK")]
    [InlineData("Remove-JavaEnvVars")]
    [InlineData("Remove-MiddlewareHome")]
    [InlineData("Remove-Domain")]
    [InlineData("Remove-OracleRegistryKeys")]
    [InlineData("Remove-VCRedist")]
    [InlineData("Remove-FormsReports")]
    [InlineData("Remove-OHS")]
    [InlineData("Drop-RCUSchemas")]
    [InlineData("RollbackOpatchApply")]
    public void GetRollbackExecutor_KnownWorkflowActions_NotNull(string action)
    {
        var logMock = new Mock<ILoggingService>();
        var factory = TestRollbackFactories.CreateProductionLikeRollbackFactory(logMock.Object);
        factory.GetRollbackExecutor(action).Should().NotBeNull($"missing rollback for '{action}'");
    }

    [Theory]
    [InlineData("Remove-AdminService")]
    [InlineData("Remove-NodeMgrService")]
    [InlineData("Remove-ms1Service")]
    [InlineData("Remove-WLS_FORMSService")]
    public void GetRollbackExecutor_RemoveServiceFallback_UsesRemoveWindowsServiceStep(string action)
    {
        var logMock = new Mock<ILoggingService>();
        var factory = TestRollbackFactories.CreateProductionLikeRollbackFactory(logMock.Object);
        factory.GetRollbackExecutor(action).Should().BeOfType<RemoveWindowsServiceStep>();
    }
}
