using System.Globalization;
using System.Windows.Data;

namespace WEDM.UI.Converters;

/// <summary>Maps progress percent and parent width to a pixel width for the wizard progress bar.</summary>
public sealed class PercentToWidthMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;

        var percent = values[0] switch
        {
            double d => d,
            int i    => i,
            _        => 0.0,
        };

        var width = values[1] switch
        {
            double d => d,
            int i    => i,
            _        => 0.0,
        };

        return Math.Clamp(percent / 100.0, 0, 1) * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
