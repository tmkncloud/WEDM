using WEDM.Domain.Enums;
using WEDM.Domain.Migration;
using Xunit;

namespace WEDM.Engine.Tests.Migration;

public sealed class MigrationVersionMatrixTests
{
    [Fact]
    public void Forms6i_AllowsFourTargets()
    {
        Assert.Equal(4, MigrationVersionMatrix.GetAllowedTargets(MiddlewareReleaseKind.Forms6i).Count);
    }

    [Fact]
    public void Forms14c_AllowsNoTargets()
    {
        Assert.Empty(MigrationVersionMatrix.GetAllowedTargets(MiddlewareReleaseKind.Forms14c));
    }

    [Fact]
    public void Forms12c_To14c_IsValid()
    {
        Assert.True(MigrationVersionMatrix.IsValidUpgradePath(
            MiddlewareReleaseKind.Forms12c,
            MiddlewareReleaseKind.Forms14c));
    }

    [Fact]
    public void Forms12c_To11g_IsInvalid()
    {
        Assert.False(MigrationVersionMatrix.IsValidUpgradePath(
            MiddlewareReleaseKind.Forms12c,
            MiddlewareReleaseKind.Forms11g));
    }
}
