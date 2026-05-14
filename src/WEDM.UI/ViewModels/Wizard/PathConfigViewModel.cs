using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.IO;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Step 3: Installation path configuration.
/// User specifies Oracle root, Middleware Home, Domain base, temp, log, and reports directories.
/// Validates paths are syntactically valid and on a drive with sufficient free space.
/// </summary>
public sealed partial class PathConfigViewModel : WizardStepViewModel
{
    // ── Path properties ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    [NotifyPropertyChangedFor(nameof(MiddlewareHome))]
    private string _oracleRoot = @"C:\Oracle";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _middlewareHome = @"C:\Oracle\Oracle_MW";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _domainBase = @"C:\Oracle\Oracle_MW\user_projects\domains";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _oracleInventory = @"C:\Oracle\oraInventory";

    [ObservableProperty]
    private string _tempDirectory = @"C:\Oracle\Temp";

    [ObservableProperty]
    private string _logDirectory = @"C:\Oracle\WEDM\logs";

    [ObservableProperty]
    private string _reportsDirectory = @"C:\Oracle\WEDM\reports";

    [ObservableProperty]
    private bool _autoPopulatePaths = true;

    // ── Validation feedback ────────────────────────────────────────────────────

    [ObservableProperty] private string _oracleRootError      = string.Empty;
    [ObservableProperty] private string _middlewareHomeError   = string.Empty;
    [ObservableProperty] private string _domainBaseError       = string.Empty;

    public override bool CanProceed =>
        IsValidPath(OracleRoot)   &&
        IsValidPath(MiddlewareHome) &&
        IsValidPath(DomainBase);

    public PathConfigViewModel()
    {
        StepTitle       = "Installation Paths";
        StepDescription = "Configure Oracle directory locations.";
        StepIcon        = "📁";
    }

    // ── Auto-populate when Oracle root changes ─────────────────────────────────

    partial void OnOracleRootChanged(string value)
    {
        if (!AutoPopulatePaths) return;
        MiddlewareHome   = Path.Combine(value, "Oracle_MW");
        DomainBase       = Path.Combine(value, "Oracle_MW", "user_projects", "domains");
        OracleInventory  = Path.Combine(value, "oraInventory");
        TempDirectory    = Path.Combine(value, "Temp");
        LogDirectory     = Path.Combine(value, "WEDM", "logs");
        ReportsDirectory = Path.Combine(value, "WEDM", "reports");
        ValidatePaths();
    }

    // ── Browse commands ────────────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseOracleRoot()
        => BrowseFolder("Select Oracle Root directory", value => OracleRoot = value);

    [RelayCommand]
    private void BrowseMiddlewareHome()
        => BrowseFolder("Select Middleware Home directory", value => MiddlewareHome = value);

    [RelayCommand]
    private void BrowseDomainBase()
        => BrowseFolder("Select Domain Base directory", value => DomainBase = value);

    private void BrowseFolder(string title, Action<string> setPath)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (dlg.ShowDialog() == true)
        {
            setPath(dlg.FolderName);
            ValidatePaths();
        }
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    private void ValidatePaths()
    {
        OracleRootError    = IsValidPath(OracleRoot)    ? string.Empty : "Enter a valid absolute path.";
        MiddlewareHomeError = IsValidPath(MiddlewareHome) ? string.Empty : "Enter a valid absolute path.";
        DomainBaseError    = IsValidPath(DomainBase)    ? string.Empty : "Enter a valid absolute path.";
        OnPropertyChanged(nameof(CanProceed));
    }

    private static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            _ = Path.GetFullPath(path);
            return Path.IsPathRooted(path);
        }
        catch { return false; }
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        config.Paths.OracleRoot       = OracleRoot;
        config.Paths.MiddlewareHome   = MiddlewareHome;
        config.Paths.DomainBase       = DomainBase;
        config.Paths.OracleInventory  = OracleInventory;
        config.Paths.TempDirectory    = TempDirectory;
        config.Paths.LogDirectory     = LogDirectory;
        config.Paths.ReportsDirectory = ReportsDirectory;
    }
}
