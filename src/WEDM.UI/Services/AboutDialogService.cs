using System.Windows;
using WEDM.Domain.Interfaces;
using WEDM.UI.ViewModels.Dialogs;
using WEDM.UI.Views.Dialogs;

namespace WEDM.UI.Services;

public sealed class AboutDialogService : IAboutDialogService
{
    private readonly IProductInfoProvider _product;

    public AboutDialogService(IProductInfoProvider product) => _product = product;

    public void ShowAbout()
    {
        var w = new AboutWindow
        {
            DataContext = new AboutViewModel(_product.GetSnapshot())
        };
        if (global::System.Windows.Application.Current.MainWindow is { } mw)
            w.Owner = mw;
        w.ShowDialog();
    }
}
