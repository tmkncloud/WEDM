using System.Globalization;
using System.Windows.Data;

namespace WEDM.UI.Converters;

[ValueConversion(typeof(string), typeof(bool))]
public sealed class StringToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
