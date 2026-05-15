using System.Xml.Linq;
using WEDM.Engine.Discovery.Parsers;
using Xunit;

namespace WEDM.Engine.Tests.Discovery;

public sealed class WebLogicConfigXmlHelperTests
{
    /// <summary>Typical 11g/12c domain config.xml: server name is a child element, not an attribute.</summary>
    private const string RealWorldConfigXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
          <name>base_domain</name>
          <admin-server-name>AdminServer</admin-server-name>
          <server>
            <name>AdminServer</name>
            <listen-address>prod-host.example.com</listen-address>
            <listen-port>7001</listen-port>
            <ssl>
              <enabled>false</enabled>
            </ssl>
          </server>
          <server>
            <name>WLS_FORMS</name>
            <listen-address>prod-host.example.com</listen-address>
            <listen-port>9001</listen-port>
            <cluster>FormsCluster</cluster>
          </server>
          <server>
            <name>WLS_REPORTS</name>
            <listen-port>9002</listen-port>
          </server>
          <cluster>
            <name>FormsCluster</name>
          </cluster>
        </domain>
        """;

    [Fact]
    public void FindServerByName_LocatesAdminServer_FromChildNameElement()
    {
        var doc = XDocument.Parse(RealWorldConfigXml);
        var admin = WebLogicConfigXmlHelper.FindServerByName(doc, "AdminServer");

        Assert.NotNull(admin);
        Assert.Equal(7001, WebLogicConfigXmlHelper.ReadChildInt(admin, "listen-port"));
    }

    [Fact]
    public void FindServerByName_LocatesManagedServers()
    {
        var doc = XDocument.Parse(RealWorldConfigXml);

        var forms = WebLogicConfigXmlHelper.FindServerByName(doc, "WLS_FORMS");
        var reports = WebLogicConfigXmlHelper.FindServerByName(doc, "WLS_REPORTS");

        Assert.NotNull(forms);
        Assert.NotNull(reports);
        Assert.Equal("FormsCluster", WebLogicConfigXmlHelper.ReadChildValue(forms, "cluster"));
        Assert.Equal(9002, WebLogicConfigXmlHelper.ReadChildInt(reports, "listen-port"));
    }

    [Fact]
    public void FindServerByName_ReturnsNull_WhenNameOnlyInAttribute()
    {
        var badXml = """
            <?xml version="1.0"?>
            <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
              <server name="AdminServer">
                <listen-port>7001</listen-port>
              </server>
            </domain>
            """;
        var doc = XDocument.Parse(badXml);
        Assert.Null(WebLogicConfigXmlHelper.FindServerByName(doc, "AdminServer"));
    }
}
