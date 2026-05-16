using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Engine.Versioning.PayloadLayouts;

public static class LocalPayloadLayoutProvider
{
    private static readonly IReadOnlyDictionary<WebLogicVersion, ILocalPayloadLayout> Layouts =
        new Dictionary<WebLogicVersion, ILocalPayloadLayout>
        {
            [WebLogicVersion.WLS_11g] = new Wls11gLocalPayloadLayout(),
            [WebLogicVersion.WLS_12c] = new Wls12cLocalPayloadLayout(),
            [WebLogicVersion.WLS_14c] = new Wls14cLocalPayloadLayout(),
            [WebLogicVersion.WLS_15c] = new Wls15cLocalPayloadLayout(),
        };

    public static ILocalPayloadLayout For(WebLogicVersion version)
        => Layouts.TryGetValue(version, out var layout)
            ? layout
            : throw new NotSupportedException($"No local payload layout for WebLogic version '{version}'.");

    public static ILocalPayloadLayout For(DeploymentConfiguration config) => For(config.WebLogicVersion);
}
