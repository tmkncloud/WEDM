using System.Windows.Controls;
using System.Windows.Input;

namespace WEDM.UI.Views.Runtime;

/// <summary>
/// Code-behind for RuntimeDetailPanelView.
/// Handles console URL click-through and RadioButton tab switching.
/// </summary>
public partial class RuntimeDetailPanelView : UserControl
{
    public RuntimeDetailPanelView()
    {
        InitializeComponent();
    }

    private void ConsoleUrl_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not WEDM.UI.ViewModels.Runtime.RuntimeDetailPanelViewModel vm) return;
        if (!vm.HasConsoleUrl) return;
        vm.OpenConsoleCommand.Execute(null);
    }
}
