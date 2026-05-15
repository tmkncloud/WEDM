using System.Text;
using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation.Wlst;

/// <summary>Generates modular, version-aware WLST scripts for migration preparation (not executed by WEDM).</summary>
public static class WlstMigrationScriptBuilder
{
    public static IReadOnlyDictionary<string, string> BuildAll(MigrationConfiguration config, MigrationContext ctx)
    {
        var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["01-create-domain.py"]       = BuildCreateDomain(ctx),
            ["02-create-machines.py"]     = BuildCreateMachines(config, ctx),
            ["03-create-clusters.py"]     = BuildCreateClusters(config, ctx),
            ["04-create-managed-servers.py"] = BuildCreateManagedServers(config, ctx),
            ["05-jdbc-resources.py"]      = BuildJdbcResources(config, ctx),
            ["06-nodemanager-enroll.py"]  = BuildNodeManagerEnroll(config, ctx),
            ["07-ssl-preparation.py"]     = BuildSslPreparation(config, ctx),
            ["08-startup-configuration.py"] = BuildStartupConfiguration(config, ctx),
        };
        return scripts;
    }

    public static string BuildCreateDomain(MigrationContext ctx)
    {
        var mw   = WlstScriptHelpers.PyRaw(ctx.TargetMiddlewareHome);
        var dom  = WlstScriptHelpers.PyRaw(ctx.TargetDomainHome);
        var tmpl = WlstScriptHelpers.PyRaw(Path.Combine(ctx.TargetMiddlewareHome, "wlserver", "common", "templates", "wls", "wls.jar"));

        var sb = new StringBuilder();
        sb.AppendLine(WlstScriptHelpers.Header("Domain recreation (offline)", ctx));
        sb.AppendLine("readTemplate(" + tmpl + ")");
        sb.AppendLine("cd('/')");
        sb.AppendLine($"set('Name', '{WlstScriptHelpers.EscapePy(ctx.DomainName)}')");
        sb.AppendLine($"cd('/Server/{WlstScriptHelpers.EscapePy(ctx.AdminServerName)}')");
        sb.AppendLine($"set('ListenAddress', '{WlstScriptHelpers.EscapePy(ctx.HostName)}')");
        sb.AppendLine($"set('ListenPort', {ctx.AdminListenPort})");
        sb.AppendLine("setOption('OverwriteDomain', 'true')");
        sb.AppendLine("setOption('ServerStartMode', 'prod')");
        sb.AppendLine("writeDomain(" + dom + ")");
        sb.AppendLine("closeTemplate()");
        sb.AppendLine("exit()");
        return sb.ToString();
    }

    public static string BuildCreateMachines(MigrationConfiguration config, MigrationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine(WlstScriptHelpers.Header("Machine creation (online)", ctx));
        sb.AppendLine("connect('weblogic', '***CHANGE_PASSWORD***', 't3://" + WlstScriptHelpers.EscapePy(ctx.HostName) + ":" + ctx.AdminListenPort + "')");
        var machineCount = Math.Max(1, config.DomainAnalysis.MachineCount);
        for (var i = 1; i <= machineCount; i++)
        {
            var name = $"Machine_{i}";
            sb.AppendLine($"# create machine {name}");
            sb.AppendLine($"cd('/')");
            sb.AppendLine($"create('{WlstScriptHelpers.EscapePy(name)}','Machine')");
        }
        sb.AppendLine("disconnect()");
        sb.AppendLine("exit()");
        return sb.ToString();
    }

    public static string BuildCreateClusters(MigrationConfiguration config, MigrationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine(WlstScriptHelpers.Header("Cluster recreation (online)", ctx));
        sb.AppendLine("connect('weblogic', '***CHANGE_PASSWORD***', 't3://" + WlstScriptHelpers.EscapePy(ctx.HostName) + ":" + ctx.AdminListenPort + "')");
        foreach (var cluster in config.Topology.Clusters)
        {
            sb.AppendLine($"create('{WlstScriptHelpers.EscapePy(cluster.Name)}','Cluster')");
        }
        if (config.Topology.Clusters.Count == 0 && config.Topology.ClusterCount > 0)
        {
            for (var i = 0; i < config.Topology.ClusterCount; i++)
                sb.AppendLine($"create('Cluster_{i + 1}','Cluster')");
        }
        sb.AppendLine("disconnect()");
        sb.AppendLine("exit()");
        return sb.ToString();
    }

    public static string BuildCreateManagedServers(MigrationConfiguration config, MigrationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine(WlstScriptHelpers.Header("Managed server recreation (online)", ctx));
        sb.AppendLine("connect('weblogic', '***CHANGE_PASSWORD***', 't3://" + WlstScriptHelpers.EscapePy(ctx.HostName) + ":" + ctx.AdminListenPort + "')");
        foreach (var ms in config.Topology.ManagedServers)
        {
            sb.AppendLine($"create('{WlstScriptHelpers.EscapePy(ms.Name)}','Server')");
            sb.AppendLine($"cd('/Server/{WlstScriptHelpers.EscapePy(ms.Name)}')");
            sb.AppendLine($"set('ListenAddress','{WlstScriptHelpers.EscapePy(ctx.HostName)}')");
            sb.AppendLine($"set('ListenPort',{ms.ListenPort})");
            if (!string.IsNullOrWhiteSpace(ms.Cluster))
                sb.AppendLine($"set('Cluster','{WlstScriptHelpers.EscapePy(ms.Cluster)}')");
        }
        sb.AppendLine("disconnect()");
        sb.AppendLine("exit()");
        return sb.ToString();
    }

    public static string BuildJdbcResources(MigrationConfiguration config, MigrationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine(WlstScriptHelpers.Header("JDBC resource recreation (online)", ctx));
        sb.AppendLine("connect('weblogic', '***CHANGE_PASSWORD***', 't3://" + WlstScriptHelpers.EscapePy(ctx.HostName) + ":" + ctx.AdminListenPort + "')");
        var count = Math.Max(0, config.DomainAnalysis.JdbcResourceCount);
        for (var i = 1; i <= count; i++)
        {
            sb.AppendLine($"# Recreate JDBC data source DS_Migration_{i}");
            sb.AppendLine($"# cd('/')");
            sb.AppendLine($"# create('DS_Migration_{i}','JDBCSystemResource')");
        }
        if (count == 0)
            sb.AppendLine("# No JDBC resources detected in source domain analysis — add data sources manually.");
        sb.AppendLine("disconnect()");
        sb.AppendLine("exit()");
        return sb.ToString();
    }

    public static string BuildNodeManagerEnroll(MigrationConfiguration config, MigrationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine(WlstScriptHelpers.Header("Node Manager enrollment (online)", ctx));
        sb.AppendLine("connect('weblogic', '***CHANGE_PASSWORD***', 't3://" + WlstScriptHelpers.EscapePy(ctx.HostName) + ":" + ctx.AdminListenPort + "')");
        sb.AppendLine("nmEnroll(" + WlstScriptHelpers.PyRaw(ctx.TargetDomainHome) + ", " + WlstScriptHelpers.PyRaw(ctx.TargetMiddlewareHome) + ")");
        sb.AppendLine("disconnect()");
        sb.AppendLine("exit()");
        return sb.ToString();
    }

    public static string BuildSslPreparation(MigrationConfiguration config, MigrationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine(WlstScriptHelpers.Header("SSL/TLS preparation (online)", ctx));
        sb.AppendLine("# Configure identity/keystore and enable TLS on admin and managed server channels.");
        sb.AppendLine("connect('weblogic', '***CHANGE_PASSWORD***', 't3://" + WlstScriptHelpers.EscapePy(ctx.HostName) + ":" + ctx.AdminListenPort + "')");
        if (!config.Topology.SslEnabled)
            sb.AppendLine("# Source domain did not have SSL enabled — plan certificate provisioning before cutover.");
        sb.AppendLine("disconnect()");
        sb.AppendLine("exit()");
        return sb.ToString();
    }

    public static string BuildStartupConfiguration(MigrationConfiguration config, MigrationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine(WlstScriptHelpers.Header("Startup argument configuration", ctx));
        sb.AppendLine("# Apply modernized JVM arguments to server start tables after domain extension.");
        foreach (var arg in config.Topology.JvmArguments.Take(20))
            sb.AppendLine($"#   {arg}");
        sb.AppendLine("exit()");
        return sb.ToString();
    }

    public static MigrationContext BuildContext(MigrationConfiguration config, string targetDomainHome)
    {
        var host = config.Target.HostName ?? config.Source.HostName ?? Environment.MachineName;
        var adminPort = config.DomainAnalysis.AdminListenPort ?? 7001;
        var domainName = config.Topology.DomainName ?? "migration_domain";
        var adminName = config.DomainAnalysis.AdminServerName ?? "AdminServer";
        var mw = config.Target.MiddlewareHome ?? config.Source.MiddlewareHome ?? @"D:\Oracle\Middleware";

        return new MigrationContext
        {
            SourceRelease         = config.Source.DisplayName,
            TargetRelease         = config.Target.DisplayName,
            Strategy              = config.Strategy.ToString(),
            TargetMiddlewareHome  = mw,
            TargetDomainHome      = targetDomainHome,
            DomainName            = domainName,
            AdminServerName       = adminName,
            HostName              = host,
            AdminListenPort       = adminPort,
        };
    }
}
