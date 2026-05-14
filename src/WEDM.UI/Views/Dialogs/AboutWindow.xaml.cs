using System.Windows;

namespace WEDM.UI.Views.Dialogs;

public partial class AboutWindow
{
    public AboutWindow() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
