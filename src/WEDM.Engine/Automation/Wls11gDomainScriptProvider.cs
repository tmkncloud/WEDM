using System.Text;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Wlst;

namespace WEDM.Engine.Automation;

/// <summary>
/// Builds a WLST offline domain-creation script for WebLogic 11g (10.3.x).
///
/// 11g notes
/// ─────────
/// • The standard wls.jar template resides under wlserver_10.3/common/templates/wls/
///   (or wlserver_12.1.2/... for 12c patch-set 1 bridges).
/// • cmo.setPassword() works in 11g and is preferred over set('Password', ...).
/// • setOption('ServerStartMode', ...) is supported from 10.3.3+; earlier builds
///   may need to configure via the StartupMode domain attribute instead.
/// </summary>
public sealed class Wls11gDomainScriptProvider : IWlstDomainScriptProvider
{
    public WebLogicVersion TargetVersion => WebLogicVersion.WLS_11g;

    public WlstScriptContext BuildCreateDomainScript(DeploymentConfiguration config)
    {
        var templatePath = MiddlewareHomePathResolver.ResolveExistingOrDefault(
            MiddlewareHomePathResolver.GetWlsTemplateJarCandidates(config.Paths.MiddlewareHome));
        var domainPath   = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);
        var adminUser    = config.Domain.AdminUsername;
        var adminPwd     = config.Domain.AdminPassword;
        var generatedAt  = DateTimeOffset.UtcNow.ToString("O");
        const string realmName = "base_domain";

        var script = BuildScript(config, templatePath, domainPath, adminUser, adminPwd,
                                 realmName, generatedAt);

        return new WlstScriptContext
        {
            ScriptContent     = script,
            TemplatePath      = templatePath,
            DomainPath        = domainPath,
            AdminUser         = adminUser,
            Version           = config.WebLogicVersion,
            GeneratedAt       = generatedAt,
            TemplateRealmName = realmName,
        };
    }

    private static string BuildScript(
        DeploymentConfiguration config,
        string templatePath,
        string domainPath,
        string adminUser,
        string adminPwd,
        string realmName,
        string generatedAt)
    {
        var tmpl = PyRaw(templatePath);
        var dom  = PyRaw(domainPath);
        var mode = config.DomainHardening.ProductionMode ? "prod" : "dev";

        var sb = new StringBuilder();

        sb.AppendLine("# WEDM-generated WLST offline domain creation");
        sb.AppendLine($"# Version:   WebLogic {config.WebLogicVersion}");
        sb.AppendLine($"# Template:  {templatePath}");
        sb.AppendLine($"# Domain:    {domainPath}");
        sb.AppendLine($"# AdminUser: {adminUser}");
        sb.AppendLine($"# Generated: {generatedAt}");
        sb.AppendLine();
        sb.AppendLine("import os");
        sb.AppendLine();

        sb.AppendLine("print('[WLST] Reading template: ' + " + tmpl + ")");
        sb.AppendLine("readTemplate(" + tmpl + ")");
        sb.AppendLine();

        sb.AppendLine("cd('/')");
        sb.AppendLine($"set('Name', '{EscapePy(config.Domain.DomainName)}')");
        sb.AppendLine();

        // 11g: cmo.setPassword() is the preferred API (forward-compatible with 12c)
        sb.AppendLine("# Admin credentials");
        sb.AppendLine($"cd('/Security/{realmName}/User/{EscapePy(adminUser)}')");
        sb.AppendLine($"cmo.setPassword('{EscapePy(adminPwd)}')");
        sb.AppendLine("cd('/')");
        sb.AppendLine();

        sb.AppendLine($"cd('/Server/{EscapePy(config.Domain.AdminServerName)}')");
        sb.AppendLine($"set('ListenAddress', '{EscapePy(config.Network.Hostname)}')");
        sb.AppendLine($"set('ListenPort', {config.Domain.AdminPort})");
        sb.AppendLine("cd('/')");
        sb.AppendLine();

        foreach (var ms in config.Domain.ManagedServers)
        {
            sb.AppendLine($"create('{EscapePy(ms.Name)}', 'Server')");
            sb.AppendLine($"cd('/Server/{EscapePy(ms.Name)}')");
            sb.AppendLine($"set('ListenAddress', '{EscapePy(config.Network.Hostname)}')");
            sb.AppendLine($"set('ListenPort', {ms.Port})");
            sb.AppendLine("cd('/')");
        }

        sb.AppendLine("setOption('OverwriteDomain', 'true')");
        sb.AppendLine($"setOption('ServerStartMode', '{mode}')");
        sb.AppendLine();

        sb.AppendLine("print('[WLST] Writing domain to: ' + " + dom + ")");
        sb.AppendLine("writeDomain(" + dom + ")");
        sb.AppendLine("closeTemplate()");
        sb.AppendLine("print('[WLST] Domain creation complete.')");
        sb.AppendLine("exit()");

        return sb.ToString();
    }

    private static string PyRaw(string path)
        => "r'" + path.Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static string EscapePy(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'",  StringComparison.Ordinal);
}
