using System.Reflection;
using System.Windows;

namespace WEDM.UI;

public partial class SplashWindow
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString() ?? "";
        ProductTitle.Text = asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "WebLogic Enterprise Deployment Manager";
        VersionLine.Text  = info;
    }
}
