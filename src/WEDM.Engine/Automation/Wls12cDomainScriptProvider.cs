using System.Text;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Wlst;

namespace WEDM.Engine.Automation;

/// <summary>
/// Builds a WLST offline domain-creation script for WebLogic 12c and 14c.
///
/// Key difference from the old (broken) approach
/// ──────────────────────────────────────────────
/// WebLogic 12c's writeDomain() internally calls ScriptExecutor.checkSecurityInfo()
/// which validates the admin user credentials in the security realm MBean tree.
///
/// The old set('Password', ...) attribute form was NEVER valid in 12c and triggers:
///   com.oracle.cie.domain.script.ScriptException: Attribute "Password" is not valid
///     at ScriptExecutor.checkSecurityInfo(...)
///     at ScriptExecutor.writeDomain(...)
///
/// The correct 12c API navigates to the admin-user MBean and calls cmo.setPassword():
///   cd('/Security/base_domain/User/weblogic')
///   cmo.setPassword('password')
///   cd('/')
///
/// The realm path uses "base_domain" — the template's internal domain name — because
/// renaming the domain via set('Name', ...) does NOT rename the security realm.
/// This provider also covers WebLogic 14c, which uses the same API surface.
/// </summary>
public sealed class Wls12cDomainScriptProvider : IWlstDomainScriptProvider
{
    /// <summary>Primary target; also used for 14c via <see cref="WlstDomainScriptProviderFactory"/>.</summary>
    public WebLogicVersion TargetVersion => WebLogicVersion.WLS_12c;

    // ── IWlstDomainScriptProvider ─────────────────────────────────────────────

    public WlstScriptContext BuildCreateDomainScript(DeploymentConfiguration config)
    {
        var templatePath = MiddlewareHomePathResolver.ResolveExistingOrDefault(
            MiddlewareHomePathResolver.GetWlsTemplateJarCandidates(config.Paths.MiddlewareHome));
        var domainPath   = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);
        var adminUser    = config.Domain.AdminUsername;
        var adminPwd     = config.Domain.AdminPassword;
        var generatedAt  = DateTimeOffset.UtcNow.ToString("O");
        const string realmName = "base_domain";   // built into wls.jar — never changes

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

    // ── Script construction ───────────────────────────────────────────────────

    private static string BuildScript(
        DeploymentConfiguration config,
        string templatePath,
        string domainPath,
        string adminUser,
        string adminPwd,
        string realmName,
        string generatedAt)
    {
        var tmpl  = PyRaw(templatePath);
        var dom   = PyRaw(domainPath);
        var mode  = config.DomainHardening.ProductionMode ? "prod" : "dev";

        var sb = new StringBuilder();

        // ── Header comment ────────────────────────────────────────────────────
        sb.AppendLine("# WEDM-generated WLST offline domain creation");
        sb.AppendLine($"# Version:   WebLogic {config.WebLogicVersion}");
        sb.AppendLine($"# Template:  {templatePath}");
        sb.AppendLine($"# Domain:    {domainPath}");
        sb.AppendLine($"# AdminUser: {adminUser}");
        sb.AppendLine($"# Generated: {generatedAt}");
        sb.AppendLine("#");
        sb.AppendLine("# NOTE: set('Password', ...) is NOT valid in 12c/14c — see comments below.");
        sb.AppendLine();
        sb.AppendLine("import os");
        sb.AppendLine();

        // ── Load template ─────────────────────────────────────────────────────
        sb.AppendLine("print('[WLST] Reading template: ' + " + tmpl + ")");
        sb.AppendLine("readTemplate(" + tmpl + ")");
        sb.AppendLine();

        // ── Domain identity ───────────────────────────────────────────────────
        sb.AppendLine("# Domain identity");
        sb.AppendLine("cd('/')");
        sb.AppendLine($"set('Name', '{EscapePy(config.Domain.DomainName)}')");
        sb.AppendLine();

        // ── Admin credentials — 12c/14c CORRECT API ───────────────────────────
        //
        // The admin user MBean lives under the template's built-in realm name.
        // wls.jar always uses 'base_domain' regardless of what you name the domain.
        //
        // WRONG (11g-era, rejected by 12c checkSecurityInfo):
        //   set('Password', 'mypassword')           ← ScriptException in writeDomain()
        //   set('UserPasswordEncrypted', ...)       ← same failure
        //
        // CORRECT for 12c and 14c:
        //   cd('/Security/base_domain/User/weblogic')
        //   cmo.setPassword('mypassword')
        //   cd('/')
        sb.AppendLine("# Admin credentials (12c/14c: cmo.setPassword via MBean — NOT set('Password',...)");
        sb.AppendLine($"cd('/Security/{realmName}/User/{EscapePy(adminUser)}')");
        sb.AppendLine($"cmo.setPassword('{EscapePy(adminPwd)}')");
        sb.AppendLine("cd('/')");
        sb.AppendLine();

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

    /// <summary>Wraps a Windows path as a Python raw string: r'C:\path\to\thing'</summary>
    private static string PyRaw(string path)
        => "r'" + path.Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>Escapes single quotes for embedding inside a Python single-quoted string literal.</summary>
    private static string EscapePy(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'",  StringComparison.Ordinal);
}
