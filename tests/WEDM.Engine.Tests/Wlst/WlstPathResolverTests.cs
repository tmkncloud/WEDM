using WEDM.Engine.Wlst;
using Xunit;

namespace WEDM.Engine.Tests.Wlst;

public sealed class WlstPathResolverTests
{
    [Fact]
    public void GetCandidates_IncludesWebLogic11gPath()
    {
        var candidates = WlstPathResolver.GetCandidates(@"D:\Oracle\Middleware");
        Assert.Contains(@"D:\Oracle\Middleware\wlserver_10.3\common\bin\wlst.cmd", candidates);
        Assert.Contains(@"D:\Oracle\Middleware\oracle_common\common\bin\wlst.cmd", candidates);
    }
}
