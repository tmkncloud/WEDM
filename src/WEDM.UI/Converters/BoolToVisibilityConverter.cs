using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WEDM.UI.Converters;

/// <summary>
/// Converts a <see cref="bool"/> to <see cref="Visibility"/>.
/// Default: true → Visible, false → Collapsed.
/// Set <see cref="Invert"/> = true for the inverse mapping.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>When true, inverts the conversion (false → Visible, true → Collapsed).</summary>
    public bool Invert { get; set; }

    /// <summary>The visibility used for the 'hidden' state. Default: Collapsed.</summary>
    public Visibility HiddenVisibility { get; set; } = Visibility.Collapsed;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is bool b && b;
        if (Invert) boolValue = !boolValue;
        return boolValue ? Visibility.Visible : HiddenVisibility;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool result = value is Visibility v && v == Visibility.Visible;
        return Invert ? !result : result;
    }
}
