using System.Xml.Linq;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;
using Xunit;

namespace WEDM.Engine.Tests.Workflow;

/// <summary>Validates deployment step config.xml checks against real WebLogic XML shapes.</summary>
public sealed class DeploymentConfigXmlValidationTests
{
    private const string ConfigXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
          <name>lab_domain</name>
          <admin-server-name>AdminServer</admin-server-name>
          <server>
            <name>AdminServer</name>
            <listen-port>7001</listen-port>
          </server>
          <server>
            <name>MS_FORMS</name>
            <listen-port>9001</listen-port>
          </server>
        </domain>
        """;

    [Fact]
    public void ConfigureAdminServerLogic_MatchesChildNameAndListenPort()
    {
        var doc = XDocument.Parse(ConfigXml);
        const string adminName = "AdminServer";
        const int expectedPort = 7001;

        var admin = WebLogicConfigXmlHelper.FindServerByName(doc, adminName);
        Assert.NotNull(admin);

        var listen = WebLogicConfigXmlHelper.ReadChildValue(admin, "listen-port");
        Assert.True(int.TryParse(listen, out var p));
        Assert.Equal(expectedPort, p);
    }

    [Fact]
    public void CreateManagedServersLogic_FindsAllConfiguredServers()
    {
        var doc = XDocument.Parse(ConfigXml);
        var names = new[] { "MS_FORMS" };

        foreach (var ms in names)
            Assert.NotNull(WebLogicConfigXmlHelper.FindServerByName(doc, ms));
    }

    [Fact]
    public void CreateManagedServersLogic_FailsWhenServerMissing()
    {
        var doc = XDocument.Parse(ConfigXml);
        Assert.Null(WebLogicConfigXmlHelper.FindServerByName(doc, "MS_MISSING"));
    }
}
