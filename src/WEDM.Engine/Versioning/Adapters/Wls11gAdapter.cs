using WEDM.Domain.Enums;

namespace WEDM.Engine.Versioning.Adapters;

/// <summary>
/// Version adapter for Oracle WebLogic 11g (10.3.6).
/// JDK 7 or 8 required; middleware layout uses "wlserver_10.3".
/// </summary>
internal sealed class Wls11gAdapter : IWebLogicVersionAdapter
{
    // ── Identity ────────────────────────────────────────────────────────────

    public WebLogicVersion Version      => WebLogicVersion.WLS_11g;
    public string          VersionLabel => "WebLogic 11g (10.3.6)";

    // ── JDK / Java ─────────────────────────────────────────────────────────

    public IReadOnlyList<string> SupportedJdkVersions => ["7", "8"];
    public string                MinJdkVersion         => "1.7.0";
    public string?               MaxJdkVersion         => "1.8.999";

    /// <summary>
    /// 11g can operate with 32-bit JDK on 32-bit platforms, but a 64-bit JDK is equally
    /// valid — this flag is false (32-bit is not *required*).
    /// </summary>
    public bool RequiresJdk32Bit => false;

    // ── Hardware requirements ───────────────────────────────────────────────

    public long MinRamMb    => 4096;
    public int  MinCpuCores => 2;
    public long MinDiskGb   => 20;

    // ── Oracle middleware paths ─────────────────────────────────────────────

    public string WlserverSubdir => "wlserver_10.3";

    public IReadOnlyList<string> WlstCmdCandidates(string middlewareHome) =>
    [
        Path.Combine(middlewareHome, "wlserver_10.3",  "common", "bin", "wlst.cmd"),
        Path.Combine(middlewareHome, "oracle_common",  "common", "bin", "wlst.cmd"),
    ];

    public IReadOnlyList<string> WlsTemplateCandidates(string middlewareHome) =>
    [
        Path.Combine(middlewareHome, "wlserver_10.3", "common", "templates", "wls", "wls.jar"),
        Path.Combine(middlewareHome, "oracle_common",  "common", "templates", "wls", "wls.jar"),
    ];

    // ── Required installer media ─────────────────────────────────────────────

    public IReadOnlyList<string> RequiredMediaPatterns =>
        ["wls1036", "fmw_11", "ofm_wls"];

    public string RequiredMediaDescription =>
        "WebLogic 11g (10.3.6) requires: wls1036_generic.jar (WebLogic Server installer) " +
        "and/or an Oracle Fusion Middleware 11g / Forms & Reports installer " +
        "(e.g. ofm_wls_generic_11.1.1.9.0_disk1_1of1.zip). " +
        "Download from Oracle eDelivery (https://edelivery.oracle.com).";

    // ── NodeManager ──────────────────────────────────────────────────────────

    public string NodeManagerDomainsPath(string middlewareHome)
        => Path.Combine(middlewareHome, "wlserver_10.3", "common", "nodemanager");

    public string NodeManagerPropertiesTemplate =>
        """
        #Node manager properties — WebLogic 11g (10.3.6)
        NodeManagerHome={NM_HOME}
        ListenAddress={LISTEN_ADDRESS}
        ListenPort={LISTEN_PORT}
        SecureListener=false
        NativeVersionEnabled=true
        LogLimit=0
        LogLevel=INFO
        DomainsFile={DOMAINS_FILE}
        DomainsFileEnabled=false
        StartScriptEnabled=false
        """;

    // ── WLST script differences ──────────────────────────────────────────────

    public bool WlstOfflineRequiresReadDomain => false;

    public string WlstCreateServerStatement(string serverName, string listenAddress, int port) =>
        $"cd('/')\n" +
        $"create('{EscapePy(serverName)}', 'Server')\n" +
        $"cd('/Server/{EscapePy(serverName)}')\n" +
        $"set('ListenAddress', '{EscapePy(listenAddress)}')\n" +
        $"set('ListenPort', {port})";

    // ── Forms & Reports ──────────────────────────────────────────────────────

    public bool SupportsFormsReports => true;
    public bool SupportsOhsWebTier   => true;

    // ── OPatch ───────────────────────────────────────────────────────────────

    public string OPatchSubdir     => "OPatch";
    public string OPatchExecutable => "opatch.bat";
    public string OPatchMinVersion => "13.8.0.0.0";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string EscapePy(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'",  "\\'",  StringComparison.Ordinal);
}
