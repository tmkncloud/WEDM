using System.Xml.Linq;
using FluentAssertions;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;
using WEDM.Engine.Tests.Fakes;
using WEDM.Engine.Workflow.Steps;
using Xunit;

namespace WEDM.Engine.Tests.Workflow;

/// <summary>Validates deployment step config.xml checks against real WebLogic XML shapes.</summary>
public sealed class DeploymentConfigXmlValidationTests
{
    // ── Config.xml fixtures ───────────────────────────────────────────────────

    /// <summary>
    /// Normal config.xml: AdminServer with explicit &lt;listen-port&gt;7001&lt;/listen-port&gt;.
    /// This is the format writeDomain produces when the port was set in WLST.
    /// </summary>
    private const string ConfigXmlWithExplicitPort = """
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

    /// <summary>
    /// Config.xml as WebLogic actually writes it when the listen port is the default (7001).
    ///
    /// WebLogic OMITS &lt;listen-port&gt; from config.xml when the port equals the default.
    /// This is the root cause of the ConfigureAdminServer "listen port mismatch" failure:
    ///   ReadChildValue returns null → int.TryParse fails → comparison fails → step fails.
    ///
    /// The fix: treat a missing &lt;listen-port&gt; element as the WebLogic default (7001).
    /// </summary>
    private const string ConfigXmlWithAbsentListenPort = """
        <?xml version="1.0" encoding="UTF-8"?>
        <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
          <name>wls_domain</name>
          <admin-server-name>AdminServer</admin-server-name>
          <server>
            <name>AdminServer</name>
            <listen-address>MOSTAFAWEBLOGIC</listen-address>
          </server>
        </domain>
        """;

    private const string ConfigXmlWithCustomPort = """
        <?xml version="1.0" encoding="UTF-8"?>
        <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
          <name>custom_domain</name>
          <admin-server-name>AdminServer</admin-server-name>
          <server>
            <name>AdminServer</name>
            <listen-port>9002</listen-port>
          </server>
        </domain>
        """;

    // ── WebLogicConfigXmlHelper logic tests ───────────────────────────────────

    [Fact]
    public void ConfigureAdminServerLogic_MatchesChildNameAndListenPort()
    {
        var doc   = XDocument.Parse(ConfigXmlWithExplicitPort);
        var admin = WebLogicConfigXmlHelper.FindServerByName(doc, "AdminServer");
        admin.Should().NotBeNull();

        var listen = WebLogicConfigXmlHelper.ReadChildValue(admin, "listen-port");
        Assert.True(int.TryParse(listen, out var p));
        Assert.Equal(7001, p);
    }

    [Fact]
    public void CreateManagedServersLogic_FindsAllConfiguredServers()
    {
        var doc = XDocument.Parse(ConfigXmlWithExplicitPort);
        WebLogicConfigXmlHelper.FindServerByName(doc, "MS_FORMS").Should().NotBeNull();
    }

    [Fact]
    public void CreateManagedServersLogic_FailsWhenServerMissing()
    {
        var doc = XDocument.Parse(ConfigXmlWithExplicitPort);
        WebLogicConfigXmlHelper.FindServerByName(doc, "MS_MISSING").Should().BeNull();
    }

    // ── Absent listen-port element — THE BUG FIX REGRESSION ─────────────────

    /// <summary>
    /// When WebLogic omits &lt;listen-port&gt; (default port scenario), ReadChildValue returns null.
    /// This is the EXACT condition that caused ConfigureAdminServerStep to always fail
    /// when AdminPort == 7001 even after a successful writeDomain.
    /// </summary>
    [Fact]
    public void ReadChildValue_returns_null_when_listen_port_element_absent()
    {
        var doc   = XDocument.Parse(ConfigXmlWithAbsentListenPort);
        var admin = WebLogicConfigXmlHelper.FindServerByName(doc, "AdminServer");
        admin.Should().NotBeNull();

        var listen = WebLogicConfigXmlHelper.ReadChildValue(admin, "listen-port");

        listen.Should().BeNullOrEmpty(
            "WebLogic omits <listen-port> from config.xml when the port is the default (7001). " +
            "ReadChildValue must return null/empty for an absent element — this is expected, not an error.");
    }

    /// <summary>
    /// Mirrors the FIXED step logic: absent element → effectivePort = 7001 → success.
    /// </summary>
    [Fact]
    public void AbsentListenPort_treated_as_default_7001_succeeds_when_configured_port_is_7001()
    {
        const int WlsDefaultListenPort = 7001;
        const int ConfiguredPort       = 7001;

        var doc   = XDocument.Parse(ConfigXmlWithAbsentListenPort);
        var admin = WebLogicConfigXmlHelper.FindServerByName(doc, "AdminServer");
        admin.Should().NotBeNull();

        var rawListenPort    = WebLogicConfigXmlHelper.ReadChildValue(admin, "listen-port");
        bool portElementAbsent = string.IsNullOrEmpty(rawListenPort);
        int  effectivePort     = portElementAbsent
            ? WlsDefaultListenPort
            : (int.TryParse(rawListenPort, out var pp) ? pp : -1);

        portElementAbsent.Should().BeTrue(
            "The absent-element case must be detected so the default fallback applies");

        effectivePort.Should().Be(WlsDefaultListenPort,
            "Absent <listen-port> means the WebLogic default (7001) is in effect");

        effectivePort.Should().Be(ConfiguredPort,
            "effectivePort must equal ConfiguredPort so the step succeeds — " +
            "this was the regression: the old code failed here every time");
    }

    /// <summary>
    /// Absent listen-port with a NON-default configured port is still a real mismatch.
    /// The step must fail — WebLogic would have written the element if a non-default was set.
    /// </summary>
    [Fact]
    public void AbsentListenPort_treated_as_default_7001_fails_when_configured_port_is_non_default()
    {
        const int WlsDefaultListenPort = 7001;
        const int ConfiguredPort       = 9001;  // non-default: would have been written to config.xml

        var doc   = XDocument.Parse(ConfigXmlWithAbsentListenPort);
        var admin = WebLogicConfigXmlHelper.FindServerByName(doc, "AdminServer");

        var rawListenPort  = WebLogicConfigXmlHelper.ReadChildValue(admin, "listen-port");
        bool portAbsent    = string.IsNullOrEmpty(rawListenPort);
        int  effectivePort = portAbsent ? WlsDefaultListenPort : (int.TryParse(rawListenPort, out var pp) ? pp : -1);

        effectivePort.Should().NotBe(ConfiguredPort,
            "When <listen-port> is absent (WebLogic default 7001) but configured port is 9001, " +
            "the step must fail — the WLST set('ListenPort', 9001) may not have taken effect.");
    }

    /// <summary>Custom port (non-default): element IS written and must match.</summary>
    [Fact]
    public void ExplicitCustomPort_9002_matches_configured_9002()
    {
        var doc   = XDocument.Parse(ConfigXmlWithCustomPort);
        var admin = WebLogicConfigXmlHelper.FindServerByName(doc, "AdminServer");

        var rawListenPort = WebLogicConfigXmlHelper.ReadChildValue(admin, "listen-port");

        rawListenPort.Should().Be("9002", "custom port is always written to config.xml");
        int.TryParse(rawListenPort, out var p).Should().BeTrue();
        p.Should().Be(9002);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// End-to-end ConfigureAdminServerStep tests using temp config.xml files
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ConfigureAdminServerStepTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "wedm-step-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLoggingService _log = new();

    public ConfigureAdminServerStepTests()
        => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DeploymentConfiguration MakeConfig(string domainName, string adminServer, int adminPort)
        => new()
        {
            WebLogicVersion = WebLogicVersion.WLS_12c,
            Paths = new PathConfiguration
            {
                MiddlewareHome = @"C:\fake\mw",
                DomainBase     = _tempDir,
                TempDirectory  = _tempDir,
            },
            Domain = new DomainConfiguration
            {
                DomainName      = domainName,
                AdminServerName = adminServer,
                AdminUsername   = "weblogic",
                AdminPassword   = "Welcome1",
                AdminPort       = adminPort,
            },
            Network         = new NetworkConfiguration { Hostname = "localhost" },
            DomainHardening = new DomainHardeningConfiguration { ProductionMode = false },
        };

    private void WriteConfigXml(string domainName, string xmlContent)
    {
        var dir = Path.Combine(_tempDir, domainName, "config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.xml"), xmlContent);
    }

    private static DeploymentStep FakeStep() => new()
    {
        Name        = "ConfigureAdminServer",
        Description = "Test step",
        IsRequired  = true,
    };

    // ── SUCCESS CASES ─────────────────────────────────────────────────────────

    /// <summary>
    /// THE PRIMARY REGRESSION TEST.
    ///
    /// Proves that ConfigureAdminServerStep succeeds when config.xml has NO &lt;listen-port&gt;
    /// element and AdminPort is configured as 7001.
    ///
    /// This is the exact failure mode seen in production:
    ///   - CreateDomain succeeds, writeDomain writes config.xml
    ///   - WebLogic omits &lt;listen-port&gt; because 7001 is the default
    ///   - ConfigureAdminServerStep read null → mismatch → exit=1
    ///   - Rollback initiated despite a perfectly created domain
    /// </summary>
    [Fact]
    public async Task Step_succeeds_when_listen_port_absent_and_configured_port_is_default_7001()
    {
        // Exactly the config.xml produced by writeDomain when ListenPort=7001 (default)
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
              <name>wls_domain</name>
              <admin-server-name>AdminServer</admin-server-name>
              <server>
                <name>AdminServer</name>
                <listen-address>MOSTAFAWEBLOGIC</listen-address>
              </server>
            </domain>
            """;

        WriteConfigXml("wls_domain", xml);
        var config = MakeConfig("wls_domain", "AdminServer", adminPort: 7001);
        var step   = new ConfigureAdminServerStep(_log);

        var result = await step.ExecuteAsync(FakeStep(), config);

        result.Success.Should().BeTrue(
            "When <listen-port> is absent from config.xml (WebLogic default), " +
            "and AdminPort == 7001 (the default), the step must succeed. " +
            "Before the fix this always failed: ReadChildValue returned null, " +
            "int.TryParse failed, the comparison was never reached.");

        result.Output.Should().Contain("7001",
            "Success output must confirm the verified port");

        // The log must explain the absent-element case
        _log.AllEntries.Should().Contain(e =>
            e.Message.Contains("absent") || e.Message.Contains("WebLogic default") || e.Message.Contains("7001"),
            "A diagnostic log must record that the element was absent and the default was applied");
    }

    [Fact]
    public async Task Step_succeeds_when_listen_port_explicitly_7001()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
              <name>wls_domain</name>
              <admin-server-name>AdminServer</admin-server-name>
              <server>
                <name>AdminServer</name>
                <listen-port>7001</listen-port>
              </server>
            </domain>
            """;

        WriteConfigXml("wls_domain", xml);
        var config = MakeConfig("wls_domain", "AdminServer", adminPort: 7001);
        var step   = new ConfigureAdminServerStep(_log);

        var result = await step.ExecuteAsync(FakeStep(), config);

        result.Success.Should().BeTrue("Explicit <listen-port>7001</listen-port> must succeed");
    }

    [Fact]
    public async Task Step_succeeds_when_non_default_port_matches()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
              <name>custom_domain</name>
              <server>
                <name>AdminServer</name>
                <listen-port>9002</listen-port>
              </server>
            </domain>
            """;

        WriteConfigXml("custom_domain", xml);
        var config = MakeConfig("custom_domain", "AdminServer", adminPort: 9002);
        var step   = new ConfigureAdminServerStep(_log);

        var result = await step.ExecuteAsync(FakeStep(), config);

        result.Success.Should().BeTrue("Non-default port 9002 matching config must succeed");
    }

    // ── FAILURE CASES ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Step_fails_when_config_xml_missing()
    {
        var config = MakeConfig("nonexistent_domain", "AdminServer", adminPort: 7001);
        var step   = new ConfigureAdminServerStep(_log);

        var result = await step.ExecuteAsync(FakeStep(), config);

        result.Success.Should().BeFalse("Missing config.xml must produce a failure");
        result.Error.Should().Contain("config.xml");
    }

    [Fact]
    public async Task Step_fails_when_admin_server_not_found_and_lists_available_servers()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
              <name>wls_domain</name>
              <server>
                <name>OtherServer</name>
                <listen-port>7001</listen-port>
              </server>
            </domain>
            """;

        WriteConfigXml("wls_domain", xml);
        var config = MakeConfig("wls_domain", "AdminServer", adminPort: 7001);
        var step   = new ConfigureAdminServerStep(_log);

        var result = await step.ExecuteAsync(FakeStep(), config);

        result.Success.Should().BeFalse("Server name mismatch must fail");
        result.Error.Should().Contain("AdminServer",
            "Failure message must name the missing server");
        result.Error.Should().Contain("OtherServer",
            "Failure message must list the servers that ARE present to help diagnosis");
    }

    [Fact]
    public async Task Step_fails_when_listen_port_absent_but_configured_port_is_non_default()
    {
        // Absent listen-port = WebLogic default 7001, but we expected 9001.
        // The WLST set('ListenPort', 9001) probably didn't take effect.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
              <name>wls_domain</name>
              <server>
                <name>AdminServer</name>
              </server>
            </domain>
            """;

        WriteConfigXml("wls_domain", xml);
        var config = MakeConfig("wls_domain", "AdminServer", adminPort: 9001);
        var step   = new ConfigureAdminServerStep(_log);

        var result = await step.ExecuteAsync(FakeStep(), config);

        result.Success.Should().BeFalse(
            "Absent listen-port (effective 7001) does not match configured port 9001 — must fail");
        result.Error.Should().Contain("9001",
            "Failure message must state the configured port that was expected");
    }

    [Fact]
    public async Task Step_fails_when_explicit_port_mismatches_configured_port()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <domain xmlns="http://xmlns.oracle.com/weblogic/domain">
              <name>wls_domain</name>
              <server>
                <name>AdminServer</name>
                <listen-port>7001</listen-port>
              </server>
            </domain>
            """;

        WriteConfigXml("wls_domain", xml);
        var config = MakeConfig("wls_domain", "AdminServer", adminPort: 9002);
        var step   = new ConfigureAdminServerStep(_log);

        var result = await step.ExecuteAsync(FakeStep(), config);

        result.Success.Should().BeFalse("Port 7001 in config.xml != configured port 9002 must fail");
        result.Error.Should().Contain("7001");
        result.Error.Should().Contain("9002");
    }
}
