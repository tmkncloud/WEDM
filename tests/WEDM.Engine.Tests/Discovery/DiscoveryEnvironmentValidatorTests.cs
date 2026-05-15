using FluentAssertions;
using WEDM.Engine.Discovery;
using Xunit;

namespace WEDM.Engine.Tests.Discovery;

public sealed class DiscoveryEnvironmentValidatorTests
{
    [Fact]
    public void Validate_RejectsEmptyPaths()
    {
        var result = DiscoveryEnvironmentValidator.Validate("", "");
        result.IsValid.Should().BeFalse();
        result.MiddlewareHomeError.Should().NotBeNullOrWhiteSpace();
        result.DomainHomeError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Validate_RejectsMissingDirectories()
    {
        var result = DiscoveryEnvironmentValidator.Validate(@"C:\NonExistent\MW", @"C:\NonExistent\Domain");
        result.IsValid.Should().BeFalse();
    }
}
