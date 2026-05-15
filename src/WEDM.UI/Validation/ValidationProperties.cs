using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WEDM.UI.Validation;

/// <summary>Attached properties for inline field validation visuals.</summary>
public static class ValidationProperties
{
    public static readonly DependencyProperty HasErrorProperty =
        DependencyProperty.RegisterAttached(
            "HasError",
            typeof(bool),
            typeof(ValidationProperties),
            new PropertyMetadata(false, OnHasErrorChanged));

    public static bool GetHasError(DependencyObject obj) => (bool)obj.GetValue(HasErrorProperty);
    public static void SetHasError(DependencyObject obj, bool value) => obj.SetValue(HasErrorProperty, value);

    private static void OnHasErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control) return;
        if ((bool)e.NewValue)
        {
            control.SetValue(Control.BorderBrushProperty,
                System.Windows.Application.Current.FindResource("DangerBrush") as Brush
                ?? Brushes.Red);
            control.SetValue(Control.BorderThicknessProperty, new Thickness(1.5));
        }
        else
        {
            control.SetValue(Control.BorderBrushProperty,
                System.Windows.Application.Current.FindResource("InputBorderBrush") as Brush
                ?? System.Windows.Application.Current.FindResource("BorderDefaultBrush") as Brush);
            control.SetValue(Control.BorderThicknessProperty, new Thickness(1));
        }
    }
}
