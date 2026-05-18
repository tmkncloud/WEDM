using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Engine.Automation;

/// <summary>
/// Generates a version-correct WLST offline Python script for WebLogic domain creation.
///
/// Implementations are version-specific because the WLST API surface changed between
/// WebLogic 11g, 12c, and 14c — most critically:
///
///   11g: set('Password', 'value') accepted in some configurations
///   12c: set('Password', ...) INVALID — triggers checkSecurityInfo() failure in writeDomain()
///        Correct API: cd('/Security/base_domain/User/weblogic'); cmo.setPassword('value')
///   14c: same as 12c, plus deprecation of some older setOption keys
///
/// Select the appropriate provider via <see cref="WlstDomainScriptProviderFactory"/>.
/// </summary>
public interface IWlstDomainScriptProvider
{
    /// <summary>Primary target version.  Implementations may also handle adjacent versions.</summary>
    WebLogicVersion TargetVersion { get; }

    /// <summary>
    /// Builds a complete WLST Python domain-creation script from the deployment configuration.
    /// The returned <see cref="WlstScriptContext"/> contains both the script text and the
    /// metadata used for diagnostics and failed-script retention.
    /// </summary>
    WlstScriptContext BuildCreateDomainScript(DeploymentConfiguration config);
}
