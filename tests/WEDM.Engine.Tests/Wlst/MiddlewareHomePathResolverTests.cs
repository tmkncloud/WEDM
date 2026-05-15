using WEDM.Engine.Wlst;
using Xunit;

namespace WEDM.Engine.Tests.Wlst;

public sealed class MiddlewareHomePathResolverTests
{
    [Fact]
    public void GetNodeManagerDomainsFileCandidates_IncludesWebLogic11gPath()
    {
        var candidates = MiddlewareHomePathResolver.GetNodeManagerDomainsFileCandidates(@"D:\Oracle\Middleware");
        Assert.Contains(@"D:\Oracle\Middleware\wlserver_10.3\common\nodemanager\nodemanager.domains", candidates);
        Assert.Contains(@"D:\Oracle\Middleware\wlserver\common\nodemanager\nodemanager.domains", candidates);
    }

    [Fact]
    public void GetWlsTemplateJarCandidates_Includes11gAnd12cLayouts()
    {
        var candidates = MiddlewareHomePathResolver.GetWlsTemplateJarCandidates(@"D:\MW");
        Assert.Contains(@"D:\MW\wlserver_10.3\common\templates\wls\wls.jar", candidates);
        Assert.Contains(@"D:\MW\wlserver\common\templates\wls\wls.jar", candidates);
    }

    [Fact]
    public void GetInstallNodeMgrSvcCandidates_Includes11gLayout()
    {
        var candidates = MiddlewareHomePathResolver.GetInstallNodeMgrSvcCandidates(@"D:\MW");
        Assert.Contains(@"D:\MW\wlserver_10.3\server\bin\installNodeMgrSvc.cmd", candidates);
    }
}
