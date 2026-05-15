using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Engine.Migration;

/// <summary>Generates believable enterprise discovery payloads for assessment demonstrations.</summary>
internal static class EnterpriseDiscoverySimulator
{
    public static (MiddlewareTopologySnapshot Topology, FormsReportsMetadataSnapshot Forms, List<EnvironmentDiscoveryFinding> Insights)
        Build(MigrationEnvironmentProfile source)
    {
        var host = source.HostName ?? Environment.MachineName;
        var (topology, forms, insights) = source.Release switch
        {
            MiddlewareReleaseKind.Forms6i  => Build6i(host, source),
            MiddlewareReleaseKind.Forms10g => Build10g(host, source),
            MiddlewareReleaseKind.Forms11g => Build11g(host, source),
            MiddlewareReleaseKind.Forms12c => Build12c(host, source),
            _                              => BuildPartial(host),
        };

        topology.ScanStatus      = DiscoveryScanStatus.Completed;
        topology.DiscoveredAtUtc = DateTimeOffset.UtcNow;
        forms.ConfigurationPath  ??= Path.Combine(source.FormsHome ?? @"D:\Oracle\Middleware\forms", "server", "forms");

        return (topology, forms, insights);
    }

    private static (MiddlewareTopologySnapshot, FormsReportsMetadataSnapshot, List<EnvironmentDiscoveryFinding>) Build6i(string host, MigrationEnvironmentProfile source)
    {
        var topology = new MiddlewareTopologySnapshot
        {
            DomainName              = "PROD_FORMS_6I",
            AdminServerUrl          = $"t3://{host}:7001",
            ManagedServerCount      = 3,
            ClusterCount            = 0,
            NodeManagerConfigured   = false,
            NodeManagerType         = "Script (legacy)",
            OhsInstances            = 1,
            SslEnabled              = false,
            SslProtocolSummary      = "Plain T3 / HTTP",
            ManagedServers =
            [
                new() { Name = "FormsServer1", ListenPort = 9001, State = "RUNNING", JvmArgsSummary = "-XX:PermSize=256m -XX:MaxPermSize=512m" },
                new() { Name = "FormsServer2", ListenPort = 9002, State = "RUNNING", JvmArgsSummary = "-XX:PermSize=256m -XX:MaxPermSize=512m" },
                new() { Name = "RepServer1", ListenPort = 9003, State = "RUNNING", JvmArgsSummary = "ConcurrentMarkSweep GC" },
            ],
            ReportsServers = [new() { Name = "RWSERVE", Url = $"http://{host}:7778/reports/rwservlet", Version = "6i" }],
            JvmArguments = ["-XX:PermSize=256m", "-XX:MaxPermSize=512m", "-XX:+UseConcMarkSweepGC", "-Djava.awt.headless=true"],
        };

        var forms = new FormsReportsMetadataSnapshot
        {
            ModuleCount = 48,
            FormCount = 342,
            ReportCount = 118,
            MenuCount = 31,
            UsesWebUtil = true,
            WebUtilModuleCount = 12,
            UsesOracleGraphics = true,
            CustomPlsqlLibraries = 19,
            TopModules = ["GL_ENTRY", "AP_INVOICE", "HR_SELF_SERVICE", "INV_SHIPMENT"],
        };

        var insights = new List<EnvironmentDiscoveryFinding>
        {
            Insight(CompatibilityRiskCategory.JvmConfiguration, "Obsolete PermGen JVM flags", "Managed servers use PermGen sizing incompatible with JDK 8+.", CompatibilitySeverity.High),
            Insight(CompatibilityRiskCategory.WebUtil, "WebUtil client modules", "12 modules declare WebUtil dependencies.", CompatibilitySeverity.Medium),
            Insight(CompatibilityRiskCategory.NodeManager, "Node Manager not enrolled", "No Node Manager listeners detected on managed server hosts.", CompatibilitySeverity.Medium),
            Insight(CompatibilityRiskCategory.SecurityHardening, "TLS not enabled on admin channel", "Administration traffic is not using SSL/TLS.", CompatibilitySeverity.Medium),
        };

        source.HostName ??= host;
        source.JavaHome ??= @"C:\Oracle\jdk1.7.0_80";
        return (topology, forms, insights);
    }

    private static (MiddlewareTopologySnapshot, FormsReportsMetadataSnapshot, List<EnvironmentDiscoveryFinding>) Build10g(string host, MigrationEnvironmentProfile source)
    {
        var topology = new MiddlewareTopologySnapshot
        {
            DomainName            = "ERP_FORMS_10G",
            AdminServerUrl        = $"t3://wl10g-{host}:7001",
            ManagedServerCount    = 5,
            ClusterCount          = 1,
            NodeManagerConfigured = true,
            NodeManagerType       = "Java",
            OhsInstances          = 2,
            SslEnabled            = true,
            SslProtocolSummary    = "TLS 1.0 (legacy cipher suites)",
            Clusters              = [new() { Name = "FormsCluster", MemberCount = 3 }],
            ManagedServers =
            [
                new() { Name = "WLS_FORMS01", Cluster = "FormsCluster", ListenPort = 8001, State = "RUNNING" },
                new() { Name = "WLS_FORMS02", Cluster = "FormsCluster", ListenPort = 8002, State = "RUNNING" },
                new() { Name = "WLS_FORMS03", Cluster = "FormsCluster", ListenPort = 8003, State = "SHUTDOWN" },
                new() { Name = "WLS_REP01", ListenPort = 8004, State = "RUNNING" },
                new() { Name = "WLS_OHS01", ListenPort = 8005, State = "RUNNING" },
            ],
            ReportsServers = [new() { Name = "REP_SVR", Url = $"https://{host}:7778/reports/rwservlet", Version = "10gR2" }],
            JvmArguments = ["-Xms2048m", "-Xmx4096m", "-XX:+UseParallelGC"],
        };

        var forms = new FormsReportsMetadataSnapshot
        {
            ModuleCount = 62, FormCount = 428, ReportCount = 136, MenuCount = 36,
            UsesWebUtil = true, WebUtilModuleCount = 8, CustomPlsqlLibraries = 24,
            TopModules = ["FIN_GL", "SCM_ORDER", "HCM_PAYROLL", "CRM_ACCOUNT"],
        };

        var insights = new List<EnvironmentDiscoveryFinding>
        {
            Insight(CompatibilityRiskCategory.Authentication, "OID authenticator configured", "Domain uses legacy OID authenticator — plan OAM/SAML mapping.", CompatibilitySeverity.High),
            Insight(CompatibilityRiskCategory.SecurityHardening, "Legacy TLS cipher suites", "SSL listeners permit TLS 1.0 — upgrade required for enterprise policy.", CompatibilitySeverity.Medium),
        };

        return (topology, forms, insights);
    }

    private static (MiddlewareTopologySnapshot, FormsReportsMetadataSnapshot, List<EnvironmentDiscoveryFinding>) Build11g(string host, MigrationEnvironmentProfile source)
    {
        var topology = new MiddlewareTopologySnapshot
        {
            DomainName            = "FMW_FORMS_11G",
            AdminServerUrl        = $"t3://fmw11g-{host}:7001",
            ManagedServerCount    = 7,
            ClusterCount          = 2,
            NodeManagerConfigured = true,
            NodeManagerType       = "SSL",
            OhsInstances          = 2,
            SslEnabled            = true,
            SslProtocolSummary    = "TLS 1.2",
            Clusters =
            [
                new() { Name = "FormsCluster_A", MemberCount = 3 },
                new() { Name = "ReportsCluster", MemberCount = 2 },
            ],
            ManagedServers =
            [
                new() { Name = "WLS_FORMS_A1", Cluster = "FormsCluster_A", ListenPort = 8101, State = "RUNNING" },
                new() { Name = "WLS_FORMS_A2", Cluster = "FormsCluster_A", ListenPort = 8102, State = "RUNNING" },
                new() { Name = "WLS_FORMS_A3", Cluster = "FormsCluster_A", ListenPort = 8103, State = "RUNNING" },
                new() { Name = "WLS_REP_B1", Cluster = "ReportsCluster", ListenPort = 8104, State = "RUNNING" },
                new() { Name = "WLS_REP_B2", Cluster = "ReportsCluster", ListenPort = 8105, State = "RUNNING" },
                new() { Name = "WLS_OHS_01", ListenPort = 8106, State = "RUNNING" },
                new() { Name = "WLS_OHS_02", ListenPort = 8107, State = "RUNNING" },
            ],
            ReportsServers = [new() { Name = "RWSVR_11G", Url = $"https://{host}:7778/reports/rwservlet", Version = "11gR2" }],
            JvmArguments = ["-Xms4096m", "-Xmx8192m", "-XX:+UseG1GC", "-Dweblogic.security.SSL.minimumProtocolVersion=TLSv1.2"],
        };

        var forms = new FormsReportsMetadataSnapshot
        {
            ModuleCount = 71, FormCount = 502, ReportCount = 148, MenuCount = 41,
            UsesWebUtil = true, WebUtilModuleCount = 4, CustomPlsqlLibraries = 17,
            TopModules = ["ERP_DASHBOARD", "PROCUREMENT", "TREASURY", "ASSETS"],
        };

        return (topology, forms, [Insight(CompatibilityRiskCategory.General, "Standard 11g topology", "Domain topology is consistent with supported in-place upgrade patterns.", CompatibilitySeverity.Informational)]);
    }

    private static (MiddlewareTopologySnapshot, FormsReportsMetadataSnapshot, List<EnvironmentDiscoveryFinding>) Build12c(string host, MigrationEnvironmentProfile source)
    {
        var topology = new MiddlewareTopologySnapshot
        {
            DomainName            = "FMW_FORMS_12C",
            AdminServerUrl        = $"t3://fmw12c-{host}:7001",
            ManagedServerCount    = 8,
            ClusterCount          = 2,
            NodeManagerConfigured = true,
            NodeManagerType       = "SSL",
            OhsInstances          = 3,
            SslEnabled            = true,
            SslProtocolSummary    = "TLS 1.2 / TLS 1.3",
            Clusters =
            [
                new() { Name = "FormsCluster", MemberCount = 4 },
                new() { Name = "ReportsCluster", MemberCount = 2 },
            ],
            ManagedServers = Enumerable.Range(1, 8).Select(i => new ManagedServerDescriptor
            {
                Name = $"WLS_SRV_{i:D2}",
                ListenPort = 8200 + i,
                State = "RUNNING",
                Cluster = i <= 4 ? "FormsCluster" : i <= 6 ? "ReportsCluster" : null,
            }).ToList(),
            ReportsServers = [new() { Name = "RWSVR_12C", Url = $"https://{host}:7778/reports/rwservlet", Version = "12.2.1.4" }],
            JvmArguments = ["-Xms6144m", "-Xmx12288m", "-XX:+UseG1GC"],
        };

        var forms = new FormsReportsMetadataSnapshot
        {
            ModuleCount = 78, FormCount = 538, ReportCount = 162, MenuCount = 44,
            UsesWebUtil = false, CustomPlsqlLibraries = 11,
            TopModules = ["CLOUD_READY_PORTAL", "ANALYTICS", "WORKFORCE", "SUPPLY_CHAIN"],
        };

        return (topology, forms, []);
    }

    private static (MiddlewareTopologySnapshot, FormsReportsMetadataSnapshot, List<EnvironmentDiscoveryFinding>) BuildPartial(string host)
    {
        return (
            new MiddlewareTopologySnapshot { DomainName = "UNKNOWN", AdminServerUrl = $"t3://{host}:7001", ScanStatus = DiscoveryScanStatus.Partial },
            new FormsReportsMetadataSnapshot(),
            [Insight(CompatibilityRiskCategory.General, "Partial discovery", "Some middleware paths were not accessible — verify credentials and paths.", CompatibilitySeverity.Medium)]);
    }

    private static EnvironmentDiscoveryFinding Insight(
        CompatibilityRiskCategory category,
        string title,
        string detail,
        CompatibilitySeverity severity) => new()
    {
        Category = category,
        Title = title,
        Detail = detail,
        Severity = severity,
    };
}
