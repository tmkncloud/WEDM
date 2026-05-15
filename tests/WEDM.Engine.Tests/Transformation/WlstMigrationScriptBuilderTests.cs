using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Transformation.Wlst;
using Xunit;

namespace WEDM.Engine.Tests.Transformation;

public sealed class WlstMigrationScriptBuilderTests
{
    [Fact]
    public void BuildCreateDomain_ContainsDomainNameAndExit()
    {
        var config = SampleConfig();
        var ctx = WlstMigrationScriptBuilder.BuildContext(config, @"D:\target\domains\base_domain");
        var script = WlstMigrationScriptBuilder.BuildCreateDomain(ctx);

        Assert.Contains("base_domain", script);
        Assert.Contains("writeDomain", script);
        Assert.Contains("exit()", script);
        Assert.Contains("DO NOT execute automatically", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAll_ProducesModularScripts()
    {
        var config = SampleConfig();
        var ctx = WlstMigrationScriptBuilder.BuildContext(config, @"D:\target\domains\base_domain");
        var scripts = WlstMigrationScriptBuilder.BuildAll(config, ctx);

        Assert.True(scripts.Count >= 6);
        Assert.All(scripts.Values, s => Assert.Contains("exit()", s));
        Assert.Contains("01-create-domain.py", scripts.Keys);
    }

    private static MigrationConfiguration SampleConfig() => new()
    {
        Name = "Test",
        Source = new MigrationEnvironmentProfile { Release = MiddlewareReleaseKind.Forms11g, DisplayName = "11g" },
        Target = new MigrationEnvironmentProfile { Release = MiddlewareReleaseKind.Forms12c, DisplayName = "12c", MiddlewareHome = @"D:\Oracle\Middleware" },
        Topology = new MiddlewareTopologySnapshot
        {
            DomainName = "base_domain",
            ManagedServers =
            [
                new ManagedServerDescriptor { Name = "MS1", ListenPort = 8001, Cluster = "ClusterA" },
            ],
            Clusters = [new ClusterDescriptor { Name = "ClusterA" }],
        },
        DomainAnalysis = new DomainAnalysisSnapshot { AdminServerName = "AdminServer", AdminListenPort = 7001 },
    };
}
