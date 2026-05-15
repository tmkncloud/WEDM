using FluentAssertions;
using WEDM.UI.Services;
using Xunit;

namespace WEDM.Engine.Tests.UI;

public sealed class WizardValidationHelperTests
{
    [Fact]
    public void IsRequiredPath_RejectsEmpty()
    {
        WizardValidationHelper.IsRequiredPath("", out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void IsRequiredPath_AcceptsRootedPath()
    {
        WizardValidationHelper.IsRequiredPath(@"C:\Oracle", out var error).Should().BeTrue();
        error.Should().BeEmpty();
    }

    [Fact]
    public void IsValidPort_RejectsOutOfRange()
    {
        WizardValidationHelper.IsValidPort(0, out _).Should().BeFalse();
        WizardValidationHelper.IsValidPort(70000, out _).Should().BeFalse();
    }

    [Fact]
    public void IsValidPort_AcceptsStandardPort()
    {
        WizardValidationHelper.IsValidPort(7001, out var error).Should().BeTrue();
        error.Should().BeEmpty();
    }
}
