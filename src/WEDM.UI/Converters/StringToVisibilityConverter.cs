using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WEDM.UI.Converters;

/// <summary>
/// Returns Visible when the string is non-null and non-empty; Collapsed otherwise.
/// Used to conditionally show error/status message panels.
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasValue = value is string s && !string.IsNullOrWhiteSpace(s);
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
