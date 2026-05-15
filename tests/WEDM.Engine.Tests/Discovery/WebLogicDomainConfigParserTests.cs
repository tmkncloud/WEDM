using WEDM.Engine.Discovery.Parsers;
using Xunit;

namespace WEDM.Engine.Tests.Discovery;

public sealed class WebLogicDomainConfigParserTests
{
    [Fact]
    public void ParseManagedServers_ReadsConfigXml()
    {
        var temp = Path.Combine(Path.GetTempPath(), "wedm-domain-test-" + Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(temp, "config");
        Directory.CreateDirectory(configDir);

        var xml = """
            <?xml version="1.0"?>
            <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
              <name>TestDomain</name>
              <admin-server-name>AdminServer</admin-server-name>
              <server>
                <name>MS1</name>
                <listen-port>8001</listen-port>
                <cluster>ClusterA</cluster>
              </server>
              <server>
                <name>AdminServer</name>
                <listen-port>7001</listen-port>
              </server>
              <cluster>
                <name>ClusterA</name>
              </cluster>
            </domain>
            """;

        File.WriteAllText(Path.Combine(configDir, "config.xml"), xml);

        var analysis = WebLogicDomainConfigParser.Parse(temp);
        var servers  = WebLogicDomainConfigParser.ParseManagedServers(temp, analysis.AdminServerName);

        Assert.Equal("TestDomain", analysis.DomainName);
        Assert.Equal(7001, analysis.AdminListenPort);
        Assert.Single(servers);
        Assert.Equal("MS1", servers[0].Name);
        Assert.Equal(8001, servers[0].ListenPort);

        Directory.Delete(temp, true);
    }
}
