using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Engine.Versioning;

/// <summary>
/// Centralises all version-specific differences between WebLogic 11g, 12c, and 14c.
/// Obtain an instance via <see cref="WebLogicVersionAdapterFactory.For(WebLogicVersion)"/>.
/// </summary>
public interface IWebLogicVersionAdapter
{
    WebLogicVersion Version { get; }

    /// <summary>Human-readable version label, e.g. "WebLogic 11g (10.3.6)".</summary>
    string VersionLabel { get; }

    // ── JDK / Java ─────────────────────────────────────────────────────────

    /// <summary>Supported JDK major version strings, e.g. ["7", "8"].</summary>
    IReadOnlyList<string> SupportedJdkVersions { get; }

    /// <summary>Minimum compatible JDK version string, e.g. "1.7.0".</summary>
    string MinJdkVersion { get; }

    /// <summary>Maximum compatible JDK version string. Null means no upper bound.</summary>
    string? MaxJdkVersion { get; }

    /// <summary>Whether a 32-bit JDK is acceptable (11g only; 12c/14c require 64-bit).</summary>
    bool RequiresJdk32Bit { get; }

    // ── Hardware requirements ───────────────────────────────────────────────

    /// <summary>Minimum RAM in megabytes.</summary>
    long MinRamMb { get; }

    /// <summary>Minimum number of CPU cores.</summary>
    int MinCpuCores { get; }

    /// <summary>Minimum free disk space in gigabytes for the Oracle middleware home.</summary>
    long MinDiskGb { get; }

    // ── Oracle middleware paths ─────────────────────────────────────────────

    /// <summary>
    /// Wlserver subdirectory name within the middleware home.
    /// "wlserver_10.3" for 11g; "wlserver" for 12c/14c.
    /// </summary>
    string WlserverSubdir { get; }

    /// <summary>
    /// Candidate wlst.cmd paths (absolute) relative to <paramref name="middlewareHome"/>,
    /// returned in priority order. The first existing path should be used.
    /// </summary>
    IReadOnlyList<string> WlstCmdCandidates(string middlewareHome);

    /// <summary>
    /// Candidate wls.jar template paths (absolute) relative to <paramref name="middlewareHome"/>,
    /// returned in priority order.
    /// </summary>
    IReadOnlyList<string> WlsTemplateCandidates(string middlewareHome);

    // ── Required installer media ─────────────────────────────────────────────

    /// <summary>
    /// Case-insensitive substrings that must appear in at least one installer file name
    /// inside the payload staging directory.
    /// </summary>
    IReadOnlyList<string> RequiredMediaPatterns { get; }

    /// <summary>Human-readable description of required installer media for remediation messages.</summary>
    string RequiredMediaDescription { get; }

    // ── NodeManager ──────────────────────────────────────────────────────────

    /// <summary>Absolute path to the NodeManager working/domains directory.</summary>
    string NodeManagerDomainsPath(string middlewareHome);

    /// <summary>
    /// Template text for nodemanager.properties; includes version-specific defaults.
    /// The caller is responsible for substituting any placeholders.
    /// </summary>
    string NodeManagerPropertiesTemplate { get; }

    // ── WLST script differences ──────────────────────────────────────────────

    /// <summary>
    /// Whether WLST offline requires an explicit <c>readDomain()</c> call before
    /// modifying server definitions. True for 12c+ extended-domain patterns; false for 11g
    /// template-based provisioning.
    /// </summary>
    bool WlstOfflineRequiresReadDomain { get; }

    /// <summary>
    /// Emits the WLST Python statement(s) that create a managed server with the
    /// given name, listen address, and port.  The returned string may be multi-line.
    /// </summary>
    string WlstCreateServerStatement(string serverName, string listenAddress, int port);

    // ── Forms & Reports ──────────────────────────────────────────────────────

    /// <summary>Whether Oracle Forms &amp; Reports is certified on this WebLogic version.</summary>
    bool SupportsFormsReports { get; }

    /// <summary>Whether Oracle HTTP Server / WebTier is certified on this WebLogic version.</summary>
    bool SupportsOhsWebTier { get; }

    // ── OPatch ───────────────────────────────────────────────────────────────

    /// <summary>OPatch subdirectory name within the middleware home, typically "OPatch".</summary>
    string OPatchSubdir { get; }

    /// <summary>OPatch executable name, e.g. "opatch.bat" on Windows.</summary>
    string OPatchExecutable { get; }

    /// <summary>Minimum OPatch version required for this WebLogic release.</summary>
    string OPatchMinVersion { get; }
}
