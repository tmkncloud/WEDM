using WEDM.Domain.Models;

namespace WEDM.UI.ViewModels.Dialogs;

public sealed class AboutViewModel
{
    public string ProductName { get; }
    public string DisplayVersion { get; }
    public string UiAssemblyFileVersion { get; }
    public string EngineAssemblyFileVersion { get; }
    public string ReleaseChannel { get; }
    public string FrameworkDescription { get; }
    public string? ReleaseNotesPath { get; }
    public string? UpdateManifestPath { get; }

    public AboutViewModel(ProductVersionSnapshot s)
    {
        ProductName               = s.ProductName;
        DisplayVersion            = s.DisplayVersion;
        UiAssemblyFileVersion     = s.UiAssemblyFileVersion;
        EngineAssemblyFileVersion = s.EngineAssemblyFileVersion;
        ReleaseChannel            = s.ReleaseChannel;
        FrameworkDescription      = s.FrameworkDescription;
        ReleaseNotesPath          = s.ReleaseNotesPath;
        UpdateManifestPath        = s.UpdateManifestPath;
    }
}
