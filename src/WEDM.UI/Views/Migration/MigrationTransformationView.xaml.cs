using System.Windows.Controls;
using System.Windows.Input;
using WEDM.UI.ViewModels.Migration;

namespace WEDM.UI.Views.Migration;

public partial class MigrationTransformationView : UserControl
{
    public MigrationTransformationView() => InitializeComponent();

    private void OpenWorkspace_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MigrationTransformationViewModel vm)
            vm.OpenWorkspaceCommand.Execute(null);
    }
}
