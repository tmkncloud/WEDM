using WEDM.Domain.Enums;
using WEDM.Engine.Versioning;

namespace WEDM.Engine.Versioning.Adapters;

internal sealed class Wls14cAdapter : IWebLogicVersionAdapter
{
    public WebLogicVersion Version      => WebLogicVersion.WLS_14c;
    public string          VersionLabel => "WebLogic 14c";

    public IReadOnlyList<string> SupportedJdkVersions => ["21"];
    public string                MinJdkVersion       => "21.0.1";
    public string?               MaxJdkVersion        => null;

    public bool RequiresJdk32Bit => false;

    public long MinRamMb    => 8192;
    public int  MinCpuCores => 4;
    public long MinDiskGb   => 40;

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
        ["fmw_14", "wls", "ofm_wls", "14.1"];

    public string RequiredMediaDescription =>
        "Fusion Middleware / WebLogic 14c generic installer from Oracle eDelivery (aligned with your SKU).";

    public string NodeManagerDomainsPath(string middlewareHome)
        => Path.Combine(middlewareHome, "wlserver", "common", "nodemanager");

    public string NodeManagerPropertiesTemplate =>
        """
        #Node manager properties — WebLogic 14c
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
