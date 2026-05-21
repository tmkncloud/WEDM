using System.Windows;
using System.Windows.Input;
using WEDM.UI.ViewModels;

namespace WEDM.UI;

/// <summary>
/// MainWindow code-behind — minimal per MVVM discipline.
/// Only window chrome interactions (drag, resize, close guard) live here.
/// All navigation and business logic lives in AppShellViewModel / MainWindowViewModel.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(AppShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    // ── Window chrome ─────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        // Guard against closing mid-deployment
        var vm = DataContext as AppShellViewModel;
        if (vm?.DeploymentInProgress == true)
        {
            var result = MessageBox.Show(
                "A deployment is currently in progress.\nAre you sure you want to exit?",
                "WebLogic Enterprise Deployment Manager",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }
        Close();
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
}
