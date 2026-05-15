namespace WEDM.UI.Services;

/// <summary>Runtime flags (environment variables / demo mode). Not for production operators.</summary>
public interface IWedmRuntimeOptions
{
  /// <summary>When true, UI may offer explicit simulation discovery (WEDM_DEMO_MODE or WEDM_ALLOW_SIMULATION).</summary>
  bool AllowDiscoverySimulation { get; }
}

public sealed class WedmRuntimeOptions : IWedmRuntimeOptions
{
  public bool AllowDiscoverySimulation =>
      IsEnabled(Environment.GetEnvironmentVariable("WEDM_DEMO_MODE"))
      || IsEnabled(Environment.GetEnvironmentVariable("WEDM_ALLOW_SIMULATION"));

  private static bool IsEnabled(string? value)
      => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
         || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
