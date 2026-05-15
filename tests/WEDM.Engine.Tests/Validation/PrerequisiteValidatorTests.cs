using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Validation;
using FluentAssertions;
using Moq;
using WEDM.Domain.Interfaces;
using WEDM.Infrastructure.Registry;
using Xunit;

namespace WEDM.Engine.Tests.Validation;

public sealed class PrerequisiteValidatorTests
{
    private readonly Mock<ILoggingService> _logMock = new();
    private readonly Mock<IPayloadAcquisitionService> _payloadMock = new();
    private readonly WindowsRegistryService _registry;
    private readonly PrerequisiteValidator _sut;

    public PrerequisiteValidatorTests()
    {
        _registry = new WindowsRegistryService(_logMock.Object);
        _payloadMock
            .Setup(p => p.ValidateAndPrepareAsync(It.IsAny<DeploymentConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrerequisiteValidationResult());
        _sut = new PrerequisiteValidator(_logMock.Object, _registry, _payloadMock.Object);
    }

    [Fact]
    public async Task ValidatePrivilegesAsync_ShouldReturnFinding()
    {
        var result = await _sut.ValidatePrivilegesAsync();
        result.Should().NotBeNull();
        result.Findings.Should().HaveCountGreaterThan(0);
        result.Findings.First().CheckName.Should().Be("AdministratorPrivileges");
    }

    [Fact]
    public async Task ValidateOperatingSystemAsync_ShouldReturnOsAndArchChecks()
    {
        var result = await _sut.ValidateOperatingSystemAsync();
        result.Findings.Should().Contain(f => f.CheckName == "OSVersion");
        result.Findings.Should().Contain(f => f.CheckName == "OSArchitecture");
    }

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    public async Task ValidateHardwareAsync_ForAllVersions_ShouldReturnRamAndCpuChecks(WebLogicVersion version)
    {
        var config = new DeploymentConfiguration { WebLogicVersion = version };
        var result = await _sut.ValidateHardwareAsync(config);
        result.Findings.Should().Contain(f => f.CheckName == "RAM");
        result.Findings.Should().Contain(f => f.CheckName == "CPU");
    }

    [Fact]
    public async Task ValidatePortsAsync_NoPorts_ShouldReturnEmptyResult()
    {
        var config = new DeploymentConfiguration();
        config.Domain.ManagedServers.Clear();
        config.Domain.AdminPort         = 0;
        config.Domain.NodeManager.Port  = 0;

        var result = await _sut.ValidatePortsAsync(config);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateVcRedistAsync_ShouldReturnVcRedistCheck()
    {
        var result = await _sut.ValidateVcRedistAsync();
        result.Findings.Should().Contain(f => f.CheckName == "VCRedist");
    }
}
