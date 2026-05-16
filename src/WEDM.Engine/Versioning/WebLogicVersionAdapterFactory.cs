using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Versioning.Adapters;

namespace WEDM.Engine.Versioning;

/// <summary>
/// Returns the correct <see cref="IWebLogicVersionAdapter"/> for a given
/// <see cref="WebLogicVersion"/> or <see cref="DeploymentConfiguration"/>.
/// </summary>
public static class WebLogicVersionAdapterFactory
{
    private static readonly IReadOnlyDictionary<WebLogicVersion, IWebLogicVersionAdapter> Adapters =
        new Dictionary<WebLogicVersion, IWebLogicVersionAdapter>
        {
            [WebLogicVersion.WLS_11g] = new Wls11gAdapter(),
            [WebLogicVersion.WLS_12c] = new Wls12cAdapter(),
            [WebLogicVersion.WLS_14c] = new Wls14cAdapter(),
            [WebLogicVersion.WLS_15c] = new Wls15cAdapter(),
        };

    /// <summary>Returns the adapter for the specified WebLogic version.</summary>
    /// <exception cref="NotSupportedException">Thrown when no adapter exists for <paramref name="version"/>.</exception>
    public static IWebLogicVersionAdapter For(WebLogicVersion version)
        => Adapters.TryGetValue(version, out var adapter)
            ? adapter
            : throw new NotSupportedException($"No adapter registered for WebLogic version '{version}'. " +
                                              $"Supported versions: {string.Join(", ", Adapters.Keys)}");

    /// <summary>Returns the adapter that matches <paramref name="config"/>'s WebLogic version.</summary>
    public static IWebLogicVersionAdapter For(DeploymentConfiguration config)
        => For(config.WebLogicVersion);

    /// <summary>Returns all registered adapters, one per supported version.</summary>
    public static IReadOnlyCollection<IWebLogicVersionAdapter> All() => Adapters.Values.ToList();
}
