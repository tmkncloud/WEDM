using System.Text;
using WEDM.Domain.Models;

namespace WEDM.Engine.Automation;

/// <summary>
/// Builds WLST offline scripts for WebLogic 12c/14c domain provisioning from <see cref="DeploymentConfiguration"/>.
/// Paths are emitted as Python raw strings (r'...') to survive Windows backslashes.
/// </summary>
public static class WlstDomainScriptBuilder
{
    public static string BuildCreateDomainPy(DeploymentConfiguration config)
    {
        var mw   = PyRaw(config.Paths.MiddlewareHome);
        var dom  = PyRaw(Path.Combine(config.Paths.DomainBase, config.Domain.DomainName));
        var tmpl = PyRaw(Path.Combine(config.Paths.MiddlewareHome, "wlserver", "common", "templates", "wls", "wls.jar"));

        var sb = new StringBuilder();
        sb.AppendLine("# WEDM-generated WLST offline domain creation");
        sb.AppendLine("import os");
        sb.AppendLine("print('Reading template: ' + " + tmpl + ")");
        sb.AppendLine("readTemplate(" + tmpl + ")");
        sb.AppendLine("cd('/')");
        sb.AppendLine($"set('Name', '{EscapePyStr(config.Domain.DomainName)}')");
        sb.AppendLine($"cd('/Server/{EscapePyStr(config.Domain.AdminServerName)}')");
        sb.AppendLine($"set('ListenAddress', '{EscapePyStr(config.Network.Hostname)}')");
        sb.AppendLine($"set('ListenPort', {config.Domain.AdminPort})");

        foreach (var ms in config.Domain.ManagedServers)
        {
            sb.AppendLine("cd('/')");
            sb.AppendLine($"create('{EscapePyStr(ms.Name)}','Server')");
            sb.AppendLine($"cd('/Server/{EscapePyStr(ms.Name)}')");
            sb.AppendLine($"set('ListenAddress', '{EscapePyStr(config.Network.Hostname)}')");
            sb.AppendLine($"set('ListenPort', {ms.Port})");
        }

        sb.AppendLine("setOption('OverwriteDomain', 'true')");
        var startMode = config.DomainHardening.ProductionMode ? "prod" : "dev";
        sb.AppendLine($"setOption('ServerStartMode', '{startMode}')");
        sb.AppendLine("print('Writing domain to: ' + " + dom + ")");
        sb.AppendLine("writeDomain(" + dom + ")");
        sb.AppendLine("closeTemplate()");
        sb.AppendLine("exit()");
        return sb.ToString();
    }

    public static string ResolveWlstCmd(DeploymentConfiguration config)
    {
        var candidates = new[]
        {
            Path.Combine(config.Paths.MiddlewareHome, "oracle_common", "common", "bin", "wlst.cmd"),
            Path.Combine(config.Paths.MiddlewareHome, "wlserver", "common", "bin", "wlst.cmd")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string PyRaw(string path)
        => "r'" + path.Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static string EscapePyStr(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}
