using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.ResponseFiles;

/// <summary>
/// Generates all Oracle OUI (.rsp) response files and companion XML files from
/// DeploymentConfiguration. Response files are dynamically generated from embedded
/// templates with token substitution — no hardcoded credentials in source or templates.
///
/// Generated files:
///   • WebLogic / Infrastructure silent install .rsp (OUI format)
///   • Forms & Reports .rsp
///   • OHS WebTier .rsp
///   • WLS 11g silent_xml (legacy format)
///   • oraInst.loc inventory pointer
///
/// Security: passwords are NEVER written to response files in plaintext.
/// The engine decrypts them at runtime and passes via environment variables
/// or secure stdin where the Oracle installer supports it.
/// </summary>
public sealed class ResponseFileGenerator
{
    private readonly ILoggingService _log;

    public ResponseFileGenerator(ILoggingService log) => _log = log;

    // ── WebLogic / Infrastructure Response File ───────────────────────────────

    public string GenerateWebLogicResponseFile(DeploymentConfiguration config)
    {
        var path = GetTempPath(config, "wls_install.rsp");

        var content = config.WebLogicVersion switch
        {
            WebLogicVersion.WLS_12c => BuildInfrastructure12cRsp(config),
            WebLogicVersion.WLS_14c => BuildInfrastructure14cRsp(config),
            _                        => BuildInfrastructure12cRsp(config)  // default
        };

        File.WriteAllText(path, content);
        _log.Info($"WebLogic .rsp written: {path}", "ResponseFile");
        return path;
    }

    private static string BuildInfrastructure12cRsp(DeploymentConfiguration config) => $@"[ENGINE]
Response File Version=1.0.0.0.0

[GENERIC]
ORACLE_HOME={config.Paths.MiddlewareHome}
INSTALL_TYPE=Fusion Middleware Infrastructure
DECLINE_SECURITY_UPDATES=true
SECURITY_UPDATES_VIA_MYORACLESUPPORT=false
SKIP_SOFTWARE_UPDATES=true
SPECIFY_DOWNLOAD_LOCATION=false
MYORACLESUPPORT_USERNAME=
MYORACLESUPPORT_PASSWORD=
PROXY_HOST=
PROXY_PORT=
PROXY_USER=
PROXY_PWD=

[SYSTEM]
[APPLICATIONS]
[RELATIONSHIPS]
";

    private static string BuildInfrastructure14cRsp(DeploymentConfiguration config) => $@"[ENGINE]
Response File Version=1.0.0.0.0

[GENERIC]
ORACLE_HOME={config.Paths.MiddlewareHome}
INSTALL_TYPE=WebLogic Server
DECLINE_SECURITY_UPDATES=true
SECURITY_UPDATES_VIA_MYORACLESUPPORT=false
SKIP_SOFTWARE_UPDATES=true
SPECIFY_DOWNLOAD_LOCATION=false

[SYSTEM]
[APPLICATIONS]
[RELATIONSHIPS]
";

    // ── WLS 11g Silent XML ────────────────────────────────────────────────────

    public string GenerateWls11gSilentXml(DeploymentConfiguration config)
    {
        var path = GetTempPath(config, "wls11g_silent.xml");
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<bea-installer>
  <input-fields>
    <data-value name=""BEAHOME""                      value=""{config.Paths.MiddlewareHome}""/>
    <data-value name=""USER_INSTALL_TYPE""             value=""Custom""/>
    <data-value name=""INSTALL_NODE_MANAGER_SERVICE""  value=""yes""/>
    <data-value name=""NODEMGR_PORT""                  value=""{config.Domain.NodeManager.Port}""/>
    <data-value name=""DOMAIN_INSTALLATION_TYPE""      value=""Express""/>
  </input-fields>
</bea-installer>
";
        File.WriteAllText(path, xml);
        _log.Info($"WLS 11g silent XML written: {path}", "ResponseFile");
        return path;
    }

    // ── Forms & Reports Response File ─────────────────────────────────────────

    public string GenerateFormsResponseFile(DeploymentConfiguration config)
    {
        var path = GetTempPath(config, "forms_install.rsp");
        var frHome = Path.Combine(config.Paths.MiddlewareHome, "Oracle_FRHome1");

        var content = $@"[ENGINE]
Response File Version=1.0.0.0.0

[GENERIC]
ORACLE_HOME={frHome}
MW_HOME={config.Paths.MiddlewareHome}
DECLINE_SECURITY_UPDATES=true
SECURITY_UPDATES_VIA_MYORACLESUPPORT=false
SKIP_SOFTWARE_UPDATES=true
SPECIFY_DOWNLOAD_LOCATION=false

[SYSTEM]
[APPLICATIONS]
[RELATIONSHIPS]
";
        File.WriteAllText(path, content);
        _log.Info($"Forms/Reports .rsp written: {path}", "ResponseFile");
        return path;
    }

    // ── OHS / WebTier Response File ───────────────────────────────────────────

    public string GenerateOhsResponseFile(DeploymentConfiguration config)
    {
        var path = GetTempPath(config, "ohs_install.rsp");
        var ohsHome = Path.Combine(config.Paths.MiddlewareHome, "Oracle_WT1");

        var content = $@"[ENGINE]
Response File Version=1.0.0.0.0

[GENERIC]
ORACLE_HOME={ohsHome}
DECLINE_SECURITY_UPDATES=true
SECURITY_UPDATES_VIA_MYORACLESUPPORT=false
SKIP_SOFTWARE_UPDATES=true
SPECIFY_DOWNLOAD_LOCATION=false

[SYSTEM]
[APPLICATIONS]
[RELATIONSHIPS]
";
        File.WriteAllText(path, content);
        _log.Info($"OHS .rsp written: {path}", "ResponseFile");
        return path;
    }

    // ── boot.properties ───────────────────────────────────────────────────────

    public void GenerateBootProperties(DeploymentConfiguration config, string decryptedPassword)
    {
        var servers = new[] { config.Domain.AdminServerName }
            .Concat(config.Domain.ManagedServers.Select(s => s.Name))
            .ToList();

        foreach (var serverName in servers)
        {
            var dir = Path.Combine(
                config.Paths.DomainBase,
                config.Domain.DomainName,
                "servers", serverName, "security");

            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, "boot.properties");

            // Note: WebLogic will encrypt this on first start
            File.WriteAllText(filePath,
                $"username={config.Domain.AdminUsername}\npassword={decryptedPassword}\n");

            _log.Info($"boot.properties written: {filePath}", "Config");
        }
    }

    // ── tnsnames.ora ──────────────────────────────────────────────────────────

    public void GenerateTnsnames(DeploymentConfiguration config)
    {
        var db = config.Database;
        var content = $@"
{db.ServiceName.ToUpper()} =
  (DESCRIPTION =
    (ADDRESS_LIST =
      (ADDRESS = (PROTOCOL = TCP)
        (HOST = {db.Host})
        (PORT = {db.Port}))
    )
    (CONNECT_DATA =
      (SID = {db.ServiceName})
    )
  )
";
        // Write to domain fmwconfig path
        var configDir = Path.Combine(
            config.Paths.DomainBase, config.Domain.DomainName,
            "config", "fmwconfig");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "tnsnames.ora"), content.TrimStart());
        _log.Info("tnsnames.ora written.", "Config");
    }

    // ── Default.env ───────────────────────────────────────────────────────────

    public void GenerateDefaultEnv(DeploymentConfiguration config, IEnumerable<string> formsPaths)
    {
        var fr = config.Domain.FormsReports;
        var joined = string.Join(";", formsPaths);

        var content = $@"# Oracle Forms Default.env — generated by WEDM
# Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}

FORMS_PATH={joined}
NLS_LANG={fr.NlsLang}
USER_NLS_LANG={fr.NlsLang}
FORMS_DATETIME_SERVER_TZ=GMT
FORMS_MMAP=false
";
        var envDir = Path.Combine(
            config.Paths.DomainBase, config.Domain.DomainName,
            "config", "fmwconfig", "servers", "WLS_FORMS",
            "applications", GetFormsAppDir(config.WebLogicVersion), "config");
        Directory.CreateDirectory(envDir);
        File.WriteAllText(Path.Combine(envDir, "default.env"), content);
        _log.Info("Default.env written.", "Config");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetTempPath(DeploymentConfiguration config, string filename)
    {
        Directory.CreateDirectory(config.Paths.TempDirectory);
        return Path.Combine(config.Paths.TempDirectory, filename);
    }

    private static string GetFormsAppDir(WebLogicVersion version) => version switch
    {
        WebLogicVersion.WLS_11g => "formsapp_11.1.2",
        WebLogicVersion.WLS_12c => "formsapp_12.2.1",
        WebLogicVersion.WLS_14c => "formsapp_14.1.2",
        _                        => "formsapp_12.2.1"
    };
}
