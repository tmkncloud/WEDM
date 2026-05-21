using System.Windows.Controls;
using WEDM.UI.ViewModels.Runtime;

namespace WEDM.UI.Views.Runtime;

/// <summary>
/// Code-behind for <see cref="RuntimeDashboardView"/>.
/// Minimal — all logic lives in <see cref="RuntimeDashboardViewModel"/>.
/// The PasswordBox is handled here because WPF PasswordBox does not support
/// two-way data binding for the Password property (by design, for security).
/// </summary>
public partial class RuntimeDashboardView : UserControl
{
    public RuntimeDashboardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Forwards the PasswordBox value to the ViewModel's AdminPassword property.
    /// PasswordBox.Password cannot be data-bound; we use a code-behind event instead.
    /// </summary>
    private void PasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is RuntimeDashboardViewModel vm && sender is PasswordBox pb)
            vm.AdminPassword = pb.Password;
    }
}
