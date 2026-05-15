using System.Windows.Controls;
using WEDM.UI.ViewModels.Migration;

namespace WEDM.UI.Views.Migration;

public partial class MigrationExecutionView : UserControl
{
    public MigrationExecutionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WirePasswordBox();
    }

    private void WirePasswordBox()
    {
        if (DataContext is not MigrationExecutionViewModel vm) return;
        WebLogicPasswordBox.PasswordChanged += (_, _) => vm.WebLogicPassword = WebLogicPasswordBox.Password;
    }
}
