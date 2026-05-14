using System.Globalization;
using System.Windows.Data;

namespace WEDM.UI.Converters;

/// <summary>
/// Converts a percentage (0–100) to a pixel width given the parent container width
/// passed as the ConverterParameter.
///
/// Binding usage (progress bar fill):
///   Width="{Binding OverallProgress,
///           Converter={StaticResource PercentToWidthConverter},
///           ConverterParameter={Binding ActualWidth, RelativeSource={RelativeSource AncestorType=Grid}}}"
///
/// Multi-binding usage (more reliable):
///   Use <see cref="PercentToWidthMultiConverter"/> below.
/// </summary>
[ValueConversion(typeof(double), typeof(double))]
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double pct) return 0d;
        if (parameter is not double totalWidth)
        {
            if (parameter is string s && double.TryParse(s, out var parsed))
                totalWidth = parsed;
            else
                totalWidth = 800d; // fallback
        }
        var width = Math.Max(0, Math.Min(1, pct / 100.0)) * totalWidth;
        return double.IsNaN(width) ? 0d : width;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Multi-value converter: [0]=percent (double 0–100), [1]=container width (double).
/// Returns the computed pixel width for the progress fill.
/// </summary>
public sealed class PercentToWidthMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0d;
        if (values[0] is not double pct)    return 0d;
        if (values[1] is not double total)  return 0d;
        if (double.IsNaN(total) || total <= 0) return 0d;
        var width = Math.Max(0, Math.Min(1, pct / 100.0)) * total;
        return double.IsNaN(width) ? 0d : width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
