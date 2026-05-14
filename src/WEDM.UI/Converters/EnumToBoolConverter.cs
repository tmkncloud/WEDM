using System.Globalization;
using System.Windows.Data;

namespace WEDM.UI.Converters;

/// <summary>
/// Converts an enum value to bool by comparing it with the ConverterParameter.
/// Used for RadioButton / ToggleButton bindings in version selection.
///
/// Usage:
///   IsChecked="{Binding SelectedVersion,
///       Converter={StaticResource EnumToBoolConverter},
///       ConverterParameter={x:Static enums:WebLogicVersion.WLS_12c}}"
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.Equals(parameter) ?? false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}
