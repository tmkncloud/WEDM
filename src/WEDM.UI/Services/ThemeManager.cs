using System.Windows;

namespace WEDM.UI.Services;

public enum WedmTheme
{
  Light,
  Dark,
}

/// <summary>Swaps merged theme resource dictionaries at runtime.</summary>
public static class ThemeManager
{
  private const string ThemeUriPrefix = "Styles/Theme.";

  public static WedmTheme Current { get; private set; } = WedmTheme.Light;

  public static event EventHandler? ThemeChanged;

  public static void Apply(WedmTheme theme)
  {
    if (System.Windows.Application.Current is null) return;

    var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
    var existing = merged.FirstOrDefault(d =>
        d.Source?.OriginalString.Contains(ThemeUriPrefix, StringComparison.OrdinalIgnoreCase) == true);

    if (existing is not null)
      merged.Remove(existing);

    var uri = new Uri($"/WEDM;component/Styles/Theme.{theme}.xaml", UriKind.Relative);
    merged.Insert(0, new ResourceDictionary { Source = uri });

    Current = theme;
    ThemeChanged?.Invoke(null, EventArgs.Empty);
  }

  public static void Toggle()
      => Apply(Current == WedmTheme.Light ? WedmTheme.Dark : WedmTheme.Light);
}
