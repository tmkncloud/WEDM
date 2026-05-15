using WEDM.Engine.Discovery.Parsers;
using Xunit;

namespace WEDM.Engine.Tests.Discovery;

public sealed class WebLogicSslDetectorTests
{
    [Fact]
    public void Analyze_ReturnsFalse_WhenSslNodesPresentButDisabled()
    {
        var temp = CreateDomain("ssl-disabled.xml");
        var result = WebLogicSslDetector.Analyze(temp);
        Assert.False(result.AnySslEnabled);
        Directory.Delete(temp, true);
    }

    [Fact]
    public void Analyze_ReturnsTrue_WhenAdminSslListenPortConfigured()
    {
        var temp = CreateDomain("ssl-enabled.xml");
        var result = WebLogicSslDetector.Analyze(temp);
        Assert.True(result.AdminSslEnabled);
        Assert.True(result.AnySslEnabled);
        Directory.Delete(temp, true);
    }

    private static string CreateDomain(string fileName)
    {
        var temp = Path.Combine(Path.GetTempPath(), "wedm-ssl-" + Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(temp, "config");
        Directory.CreateDirectory(configDir);
        var xml = fileName switch
        {
            "ssl-enabled.xml" => """
                <?xml version="1.0"?>
                <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
                  <name>TestDomain</name>
                  <admin-server-name>AdminServer</admin-server-name>
                  <server>
                    <name>AdminServer</name>
                    <listen-port>7001</listen-port>
                    <ssl>
                      <listen-port>7002</listen-port>
                    </ssl>
                  </server>
                </domain>
                """,
            _ => """
                <?xml version="1.0"?>
                <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
                  <name>TestDomain</name>
                  <admin-server-name>AdminServer</admin-server-name>
                  <server>
                    <name>AdminServer</name>
                    <listen-port>7001</listen-port>
                    <ssl enabled="false"/>
                  </server>
                </domain>
                """
        };
        File.WriteAllText(Path.Combine(configDir, "config.xml"), xml);
        return temp;
    }
}
