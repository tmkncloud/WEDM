using WEDM.Domain.Enums;
using WEDM.Engine.Versioning;

namespace WEDM.Engine.Versioning.Adapters;

internal sealed class Wls12cAdapter : IWebLogicVersionAdapter
{
    public WebLogicVersion Version      => WebLogicVersion.WLS_12c;
    public string          VersionLabel => "WebLogic 12c (12.2.1.x)";

    public IReadOnlyList<string> SupportedJdkVersions => ["8"];
    public string                MinJdkVersion       => "1.8.0";
    public string?               MaxJdkVersion        => "1.8.999";

    public bool RequiresJdk32Bit => false;

    public long MinRamMb    => 8192;
    public int  MinCpuCores => 2;
    public long MinDiskGb   => 30;

    public string WlserverSubdir => "wlserver";

    public IReadOnlyList<string> WlstCmdCandidates(string middlewareHome) =>
    [
        Path.Combine(middlewareHome, "oracle_common", "common", "bin", "wlst.cmd"),
        Path.Combine(middlewareHome, "wlserver", "common", "bin", "wlst.cmd"),
    ];

    public IReadOnlyList<string> WlsTemplateCandidates(string middlewareHome) =>
    [
        Path.Combine(middlewareHome, "wlserver", "common", "templates", "wls", "wls.jar"),
    ];

    public IReadOnlyList<string> RequiredMediaPatterns =>
        ["fmw_12", "wls", "ofm_wls", "1221"];

    public string RequiredMediaDescription =>
        "Fusion Middleware 12c / WebLogic 12.2.1.x generic installer or JAR bundle from Oracle eDelivery.";

    public string NodeManagerDomainsPath(string middlewareHome)
        => Path.Combine(middlewareHome, "wlserver", "common", "nodemanager");

    public string NodeManagerPropertiesTemplate =>
        """
        #Node manager properties — WebLogic 12c
        NodeManagerHome={NM_HOME}
        ListenAddress={LISTEN_ADDRESS}
        ListenPort={LISTEN_PORT}
        SecureListener=false
        NativeVersionEnabled=true
        LogLevel=INFO
        DomainsFile={DOMAINS_FILE}
        DomainsFileEnabled=true
        """;

    public bool WlstOfflineRequiresReadDomain => true;

    public string WlstCreateServerStatement(string serverName, string listenAddress, int port) =>
        $"cd('/Servers')\n" +
        $"create('{EscapePy(serverName)}', 'Server')\n" +
        $"cd('/Servers/{EscapePy(serverName)}')\n" +
        $"set('ListenAddress', '{EscapePy(listenAddress)}')\n" +
        $"set('ListenPort', {port})";

    public bool SupportsFormsReports => true;
    public bool SupportsOhsWebTier   => true;

    public string OPatchSubdir     => "OPatch";
    public string OPatchExecutable => "opatch.bat";
    public string OPatchMinVersion => "13.9.4.2.0";

    private static string EscapePy(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
}
