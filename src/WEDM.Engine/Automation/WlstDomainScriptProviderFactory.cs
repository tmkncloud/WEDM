using WEDM.Domain.Enums;

namespace WEDM.Engine.Automation;

/// <summary>
/// Selects and instantiates the correct <see cref="IWlstDomainScriptProvider"/> for a
/// given WebLogic version.
///
/// Mapping
/// ───────
/// WLS_11g  → <see cref="Wls11gDomainScriptProvider"/>
/// WLS_12c  → <see cref="Wls12cDomainScriptProvider"/>
/// WLS_14c  → <see cref="Wls12cDomainScriptProvider"/> (identical API surface to 12c)
/// WLS_15c  → <see cref="Wls12cDomainScriptProvider"/> (identical API surface to 12c/14c)
/// Unknown  → <see cref="Wls12cDomainScriptProvider"/> (safe default)
/// </summary>
public static class WlstDomainScriptProviderFactory
{
    /// <summary>
    /// Returns a provider that generates a WLST domain-creation script compatible with
    /// the specified WebLogic version.
    /// </summary>
    public static IWlstDomainScriptProvider Create(WebLogicVersion version)
        => version switch
        {
            WebLogicVersion.WLS_11g => new Wls11gDomainScriptProvider(),
            WebLogicVersion.WLS_14c => new Wls12cDomainScriptProvider(), // same API as 12c
            WebLogicVersion.WLS_15c => new Wls12cDomainScriptProvider(), // same API as 12c/14c
            _                       => new Wls12cDomainScriptProvider(), // 12c is the safe default
        };
}
