using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows;
using WEDM.Domain.Interfaces;
using WEDM.UI.Models;
using WEDM.UI.Services;
using WEDM.UI.ViewModels.Runtime;

namespace WEDM.UI.ViewModels;

/// <summary>
/// Top-level shell ViewModel — owns the activity bar, section panel visibility,
/// and the content host's active view. Wraps <see cref="MainWindowViewModel"/>
/// (wizard) and <see cref="RuntimeDashboardViewModel"/> (runtime) without
/// duplicating their logic.
///
/// Navigation flow:
///   ActivityBar click → NavigateToCommand(AppSection)
///   → INavigationService.NavigateTo()
///   → SectionChanged event
///   → OnSectionChanged() updates ActivityItems.IsActive + ActiveContent
/// </summary>
public sealed partial class AppShellViewModel : ObservableObject
{
    private readonly INavigationService _nav;
    private readonly MainWindowViewModel _wizardVm;
    private readonly RuntimeDashboardViewModel _runtimeVm;

    // ── Observable state ─────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSectionWizardMode))]
    [NotifyPropertyChangedFor(nameof(IsRuntimeSection))]
    [NotifyPropertyChangedFor(nameof(SectionPanelTitle))]
    private AppSection _activeSection = AppSection.Dashboard;

    /// <summary>
    /// The object bound to the main ContentPresenter.
    /// DataTemplates in MainWindow.xaml resolve this to the correct view.
    /// </summary>
    [ObservableProperty]
    private object? _activeContent;

    [ObservableProperty]
    private bool _isSectionPanelVisible = true;

    [ObservableProperty]
    private string _themeToggleLabel = "Dark mode";

    [ObservableProperty]
    private string _appVersion = "v1.0.0";

    // ── Computed properties ───────────────────────────────────────────────────

    /// <summary>True when the Install section is active (wizard sub-panels shown).</summary>
    public bool IsSectionWizardMode => ActiveSection == AppSection.Install;

    /// <summary>True when the Runtime section is active.</summary>
    public bool IsRuntimeSection => ActiveSection == AppSection.Runtime;

    /// <summary>Forward deployment-in-progress flag for window close guard.</summary>
    public bool DeploymentInProgress => _wizardVm.DeploymentInProgress;

    /// <summary>Panel header text driven by active section.</summary>
    public string SectionPanelTitle => ActiveSection switch
    {
        AppSection.Dashboard    => "Dashboard",
        AppSection.Install      => "Install",
        AppSection.Runtime      => "Runtime",
        AppSection.Discovery    => "Discovery",
        AppSection.Deployments  => "Deployments",
        AppSection.Logs         => "Logs",
        AppSection.Reports      => "Reports",
        AppSection.Settings     => "Settings",
        _                       => string.Empty
    };

    /// <summary>Exposed so SectionPanelView can bind wizard steps when Install is active.</summary>
    public MainWindowViewModel WizardVm => _wizardVm;

    /// <summary>Exposed for StatusBar runtime status binding.</summary>
    public RuntimeDashboardViewModel RuntimeVm => _runtimeVm;

    // ── Activity bar items ────────────────────────────────────────────────────

    public ObservableCollection<ActivityBarItemViewModel> TopItems { get; } = [];
    public ObservableCollection<ActivityBarItemViewModel> BottomItems { get; } = [];

    // ── Constructor ───────────────────────────────────────────────────────────

    public AppShellViewModel(
        INavigationService nav,
        MainWindowViewModel wizardVm,
        RuntimeDashboardViewModel runtimeVm,
        IProductInfoProvider productInfo)
    {
        _nav = nav;
        _wizardVm = wizardVm;
        _runtimeVm = runtimeVm;

        AppVersion = productInfo.GetSnapshot().DisplayVersion;
        ThemeToggleLabel = ThemeManager.Current == WedmTheme.Light ? "Dark mode" : "Light mode";

        BuildActivityItems();

        _nav.SectionChanged += (_, section) => OnSectionChanged(section);

        // Forward DeploymentInProgress changes from wizard VM
        _wizardVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.DeploymentInProgress))
                OnPropertyChanged(nameof(DeploymentInProgress));
        };

        // Activate default section
        ActivateSection(AppSection.Dashboard);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void NavigateTo(AppSection section) => _nav.NavigateTo(section);

    [RelayCommand]
    private void ToggleSectionPanel() => IsSectionPanelVisible = !IsSectionPanelVisible;

    [RelayCommand]
    private void ToggleTheme()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ThemeManager.Toggle();
            ThemeToggleLabel = ThemeManager.Current == WedmTheme.Light ? "Dark mode" : "Light mode";
        });
    }

    [RelayCommand]
    private void OpenHelp() => _wizardVm.OpenHelpCommand.Execute(null);

    [RelayCommand]
    private void ShowAbout() => _wizardVm.ShowAboutCommand.Execute(null);

    // ── Navigation logic ──────────────────────────────────────────────────────

    private void OnSectionChanged(AppSection section)
    {
        Application.Current.Dispatcher.Invoke(() => ActivateSection(section));
    }

    private void ActivateSection(AppSection section)
    {
        ActiveSection = section;

        // Update activity item active states
        foreach (var item in TopItems)
            item.IsActive = item.Section == section;
        foreach (var item in BottomItems)
            item.IsActive = item.Section == section;

        // Resolve the content object for the ContentPresenter
        ActiveContent = section switch
        {
            AppSection.Install  => _wizardVm,
            AppSection.Runtime  => _runtimeVm,
            _                   => null   // placeholder — will render section placeholder view
        };
    }

    // ── Activity bar construction ─────────────────────────────────────────────

    private void BuildActivityItems()
    {
        // Segoe MDL2 Assets glyphs
        TopItems.Add(new ActivityBarItemViewModel(AppSection.Dashboard,   "", "Dashboard",   _nav));
        TopItems.Add(new ActivityBarItemViewModel(AppSection.Install,     "", "Install",      _nav));
        TopItems.Add(new ActivityBarItemViewModel(AppSection.Runtime,     "", "Runtime",      _nav));
        TopItems.Add(new ActivityBarItemViewModel(AppSection.Discovery,   "", "Discovery",    _nav));
        TopItems.Add(new ActivityBarItemViewModel(AppSection.Deployments, "", "Deployments",  _nav));
        TopItems.Add(new ActivityBarItemViewModel(AppSection.Logs,        "", "Logs",         _nav));
        TopItems.Add(new ActivityBarItemViewModel(AppSection.Reports,     "", "Reports",      _nav));

        BottomItems.Add(new ActivityBarItemViewModel(AppSection.Settings, "", "Settings",     _nav));
    }
}
