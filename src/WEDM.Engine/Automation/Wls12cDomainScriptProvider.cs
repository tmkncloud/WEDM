using System.Text;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Wlst;

namespace WEDM.Engine.Automation;

/// <summary>
/// Builds a WLST offline domain-creation script for WebLogic 12c, 14c, and 15c.
///
/// Admin credential approach
/// ─────────────────────────
/// writeDomain() calls checkSecurityInfo() internally which validates the admin-user
/// MBean.  The correct API is cmo.setPassword() via MBean navigation — NOT the
/// rejected set('Password', ...) attribute form.
///
/// The admin user lives at /Security/{realm}/User/{adminUser}, but the realm name
/// varies across WLS versions, patch levels, and templates:
///   wls.jar on WLS 12.2.1.4  → realm may be 'myrealm' or 'base_domain'
///   wls.jar on WLS 14.1.1    → realm is typically 'myrealm'
///
/// This provider emits the _wedm_discover_admin_path() Jython helper which
/// discovers the correct path at runtime by calling ls() after readTemplate().
/// No realm name is ever hardcoded in the generated script.
/// </summary>
public sealed class Wls12cDomainScriptProvider : IWlstDomainScriptProvider
{
    /// <summary>Primary target; also used for 14c/15c via <see cref="WlstDomainScriptProviderFactory"/>.</summary>
    public WebLogicVersion TargetVersion => WebLogicVersion.WLS_12c;

    // ── IWlstDomainScriptProvider ─────────────────────────────────────────────

    public WlstScriptContext BuildCreateDomainScript(DeploymentConfiguration config)
    {
        var templatePath = MiddlewareHomePathResolver.ResolveExistingOrDefault(
            MiddlewareHomePathResolver.GetWlsTemplateJarCandidates(config.Paths.MiddlewareHome));
        var domainPath  = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);
        var adminUser   = config.Domain.AdminUsername;
        var adminPwd    = config.Domain.AdminPassword;
        var generatedAt = DateTimeOffset.UtcNow.ToString("O");

        var script = BuildScript(config, templatePath, domainPath, adminUser, adminPwd, generatedAt);

        return new WlstScriptContext
        {
            ScriptContent     = script,
            TemplatePath      = templatePath,
            DomainPath        = domainPath,
            AdminUser         = adminUser,
            Version           = config.WebLogicVersion,
            GeneratedAt       = generatedAt,
            TemplateRealmName = "discovered-at-runtime",   // dynamic — not hardcoded
        };
    }

    // ── Script construction ───────────────────────────────────────────────────

    private static string BuildScript(
        DeploymentConfiguration config,
        string templatePath,
        string domainPath,
        string adminUser,
        string adminPwd,
        string generatedAt)
    {
        var tmpl = PyRaw(templatePath);
        var dom  = PyRaw(domainPath);
        var mode = config.DomainHardening.ProductionMode ? "prod" : "dev";

        var sb = new StringBuilder();

        // ── Header ────────────────────────────────────────────────────────────
        sb.AppendLine("# WEDM-generated WLST offline domain creation");
        sb.AppendLine($"# Version:   WebLogic {config.WebLogicVersion}");
        sb.AppendLine($"# Template:  {templatePath}");
        sb.AppendLine($"# Domain:    {domainPath}");
        sb.AppendLine($"# AdminUser: {adminUser}");
        sb.AppendLine($"# Generated: {generatedAt}");
        sb.AppendLine("#");
        sb.AppendLine("# Admin credential method: cmo.setPassword() via dynamic MBean discovery.");
        sb.AppendLine("# Realm name is discovered at runtime — no hardcoded /Security/<name>/ path.");
        sb.AppendLine();
        sb.AppendLine("import os");
        sb.AppendLine();

        // ── Runtime discovery helpers (emitted before use) ────────────────────
        WlstDomainScriptHelpers.AppendAdminDiscoveryHelpers(sb);

        // ── Load template ─────────────────────────────────────────────────────
        sb.AppendLine("print('[WLST] Reading template: ' + " + tmpl + ")");
        sb.AppendLine("readTemplate(" + tmpl + ")");
        sb.AppendLine();

        // ── Domain identity ───────────────────────────────────────────────────
        sb.AppendLine("# Domain identity");
        sb.AppendLine("cd('/')");
        sb.AppendLine($"set('Name', '{EscapePy(config.Domain.DomainName)}')");
        sb.AppendLine();

        // ── Admin credentials (dynamic discovery + cmo.setPassword) ──────────
        WlstDomainScriptHelpers.AppendAdminCredentialBlock(sb, adminUser, adminPwd);

        // ── AdminServer ───────────────────────────────────────────────────────
        sb.AppendLine("# AdminServer");
        sb.AppendLine($"cd('/Server/{EscapePy(config.Domain.AdminServerName)}')");
        sb.AppendLine($"set('ListenAddress', '{EscapePy(config.Network.Hostname)}')");
        sb.AppendLine($"set('ListenPort', {config.Domain.AdminPort})");
        sb.AppendLine("cd('/')");
        sb.AppendLine();

        // ── Managed servers ───────────────────────────────────────────────────
        if (config.Domain.ManagedServers.Count > 0)
        {
            sb.AppendLine("# Managed servers");
            foreach (var ms in config.Domain.ManagedServers)
            {
                sb.AppendLine($"create('{EscapePy(ms.Name)}', 'Server')");
                sb.AppendLine($"cd('/Server/{EscapePy(ms.Name)}')");
                sb.AppendLine($"set('ListenAddress', '{EscapePy(config.Network.Hostname)}')");
                sb.AppendLine($"set('ListenPort', {ms.Port})");
                sb.AppendLine("cd('/')");
            }
            sb.AppendLine();
        }

        // ── Domain options ────────────────────────────────────────────────────
        sb.AppendLine("# Domain options");
        sb.AppendLine("setOption('OverwriteDomain', 'true')");
        sb.AppendLine($"setOption('ServerStartMode', '{mode}')");
        sb.AppendLine();

        // ── Write domain ──────────────────────────────────────────────────────
        sb.AppendLine("print('[WLST] Writing domain to: ' + " + dom + ")");
        sb.AppendLine("writeDomain(" + dom + ")");
        sb.AppendLine("closeTemplate()");
        sb.AppendLine("print('[WLST] Domain creation complete.')");
        sb.AppendLine("exit()");

        return sb.ToString();
    }

    // ── Python string helpers ─────────────────────────────────────────────────

    private static string PyRaw(string path)
        => "r'" + path.Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static string EscapePy(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'",  StringComparison.Ordinal);
}
