using WEDM.Engine.Payload;
using Xunit;

namespace WEDM.Engine.Tests.Payload;

public sealed class InstallerExitCodesTests
{
    [Theory]
    [InlineData(0, true, "Installed Successfully")]
    [InlineData(3010, true, "Reboot Required")]
    [InlineData(1638, true, "Already Installed")]
    [InlineData(1603, false, "Failed")]
    public void IsSuccess_AndDescribe(int code, bool expected, string fragment)
    {
        Assert.Equal(expected, InstallerExitCodes.IsSuccess(code));
        Assert.Contains(fragment, InstallerExitCodes.DescribeOutcome(code));
    }
}
