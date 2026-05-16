using WEDM.Application.Services;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Opatch;
using WEDM.Infrastructure.Deployment;
using WEDM.Infrastructure.Security;
using FluentAssertions;
using Moq;
using Xunit;

namespace WEDM.Application.Tests.Services;

public sealed class DeploymentOrchestratorTests
{
    private readonly Mock<IWorkflowOrchestrator> _workflowMock  = new();
    private readonly Mock<IValidationEngine>      _validatorMock = new();
    private readonly Mock<ILoggingService>         _logMock       = new();
    private readonly Mock<IPowerShellExecutor>     _psMock        = new();
    private readonly Mock<IOperationalTelemetrySink> _telemetryMock = new();

    private DeploymentOrchestrator CreateSut(string? sessionRoot = null, string? lockRoot = null)
    {
        var opatch = new OpatchRunner(_psMock.Object, _logMock.Object);
        return new(
            _workflowMock.Object,
            _validatorMock.Object,
            opatch,
            _logMock.Object,
            _telemetryMock.Object,
            new JsonDeploymentSessionStore(sessionRoot ?? Path.Combine(Path.GetTempPath(), "wedm-test-sessions", Guid.NewGuid().ToString("N"))),
            new DeploymentLockService(lockRoot ?? Path.Combine(Path.GetTempPath(), "wedm-test-locks", Guid.NewGuid().ToString("N"))),
            new DeploymentSecretLifecycleService());
    }

    [Fact]
    public async Task ExecuteDeploymentAsync_WhenPrereqsFail_ReturnsFailed()
    {
        // Arrange
        var config = new DeploymentConfiguration();
        config.Database.RunRcu = false;

        var failedResult = PrerequisiteValidationResult.New(config.Id);
        failedResult.Fatal("Test", "Simulated fatal check failure");

        _validatorMock.Setup(v => v.ValidatePrivilegesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(failedResult);
        _validatorMock.Setup(v => v.ValidateOperatingSystemAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrerequisiteValidationResult.New(config.Id));
        _validatorMock.Setup(v => v.ValidateHardwareAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrerequisiteValidationResult.New(config.Id));
        _validatorMock.Setup(v => v.ValidateDiskSpaceAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrerequisiteValidationResult.New(config.Id));
        _validatorMock.Setup(v => v.ValidatePortsAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrerequisiteValidationResult.New(config.Id));
        _validatorMock.Setup(v => v.ValidateJavaAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrerequisiteValidationResult.New(config.Id));
        _validatorMock.Setup(v => v.ValidateVcRedistAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrerequisiteValidationResult.New(config.Id));
        _validatorMock.Setup(v => v.ValidatePayloadIntegrityAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrerequisiteValidationResult.New(config.Id));

        var sut = CreateSut();

        // Act
        var report = await sut.ExecuteDeploymentAsync(config, CancellationToken.None);

        // Assert
        report.FinalStatus.Should().Be(DeploymentStatus.Failed);
        _workflowMock.Verify(w => w.RunAsync(It.IsAny<DeploymentConfiguration>(),
            It.IsAny<IReadOnlyList<DeploymentStep>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
