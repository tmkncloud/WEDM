using System.Text;
using WEDM.Domain.Models;

namespace WEDM.Engine.Automation;

/// <summary>Builds WLST online (connected) scripts for nmEnroll, production mode, and machine mapping.</summary>
public static class WlstOnlineScriptBuilder
{
    public static string BuildPostBootOnlinePy(DeploymentConfiguration config)
    {
        var machine = EscapePyStr(config.Domain.Machine);
        var admin   = EscapePyStr(config.Domain.AdminServerName);
        var domHome = PyRaw(Path.Combine(config.Paths.DomainBase, config.Domain.DomainName));

        var sb = new StringBuilder();
        sb.AppendLine("# WEDM-generated WLST online post-boot automation");
        sb.AppendLine("import os");
        sb.AppendLine("adminUser = os.environ['WEDM_ADMIN_USER']");
        sb.AppendLine("adminPass = os.environ['WEDM_ADMIN_PASS']");
        sb.AppendLine("adminUrl = os.environ['WEDM_ADMIN_URL']");
        sb.AppendLine("runNm = os.environ.get('WEDM_RUN_NM_ENROLL', '0') == '1'");
        sb.AppendLine("applyHardening = os.environ.get('WEDM_APPLY_ONLINE_HARDENING', '0') == '1'");
        sb.AppendLine("domainHome = " + domHome);
        sb.AppendLine("machineName = '" + machine + "'");
        sb.AppendLine("adminServer = '" + admin + "'");
        sb.AppendLine("try:");
        sb.AppendLine("  print('[WEDM] Connecting: ' + adminUrl)");
        sb.AppendLine("  connect(adminUser, adminPass, adminUrl)");
        sb.AppendLine("  print('[WEDM] Connected')");
        sb.AppendLine("  if runNm:");
        sb.AppendLine("    print('[WEDM] nmEnroll')");
        sb.AppendLine("    nmEnroll(domainHome)");
        sb.AppendLine("  if applyHardening:");
        sb.AppendLine("    edit()");
        sb.AppendLine("    startEdit()");
        sb.AppendLine("    cd('/')");
        sb.AppendLine("    cmo.setProductionModeEnabled(" + (config.DomainHardening.ProductionMode ? "1" : "0") + ")");
        sb.AppendLine("    cd('/Machines')");
        sb.AppendLine("    try:");
        sb.AppendLine("      create(machineName, 'Machine')");
        sb.AppendLine("    except Exception, ex1:");
        sb.AppendLine("      print('[WEDM] Machine create: ' + str(ex1))");
        sb.AppendLine("    cd('/Servers/' + adminServer)");
        sb.AppendLine("    set('Machine', machineName)");
        foreach (var ms in config.Domain.ManagedServers)
        {
            var n = EscapePyStr(ms.Name);
            sb.AppendLine($"    cd('/Servers/{n}')");
            sb.AppendLine("    set('Machine', machineName)");
        }

        sb.AppendLine("    activate(block='true')");
        sb.AppendLine("finally:");
        sb.AppendLine("  try:");
        sb.AppendLine("    disconnect()");
        sb.AppendLine("  except Exception, ex2:");
        sb.AppendLine("    pass");
        sb.AppendLine("exit()");
        return sb.ToString();
    }

    private static string PyRaw(string path)
        => "r'" + path.Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static string EscapePyStr(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}
