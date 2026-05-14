using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.UI.Services;
using WEDM.UI.ViewModels.Base;
using WEDM.UI.ViewModels.Wizard;

namespace WEDM.UI.ViewModels;

/// <summary>
/// Root ViewModel — owns the wizard step collection, navigation state,
/// the shared DeploymentConfiguration, and overall application lifecycle.
///
/// Navigation model:
///   Steps are indexed 0..N. CurrentStepIndex drives the active view.
///   MainWindow data-binds CurrentStep to a ContentPresenter with DataTemplate selectors.
///
/// The configuration object is shared across all wizard steps via reference.
/// Each step's ApplyToConfiguration() is called on forward navigation.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IReadOnlyList<WizardStepViewModel> _steps;
    private readonly IAboutDialogService _aboutDialog;

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStep))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(IsDeploymentReadyStep))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private int _currentStepIndex;

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [ObservableProperty]
    private string _appVersion = "v1.0.0";

    [ObservableProperty]
    private string _releaseChannelDisplay = "Stable";

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private bool _deploymentInProgress;

    public DeploymentConfiguration Configuration { get; } = new DeploymentConfiguration
    {
        Name = "New WebLogic Deployment"
    };

    // ── Computed ──────────────────────────────────────────────────────────────

    public WizardStepViewModel CurrentStep => _steps[CurrentStepIndex];
    public IReadOnlyList<WizardStepViewModel> Steps => _steps;
    public bool CanGoBack    => CurrentStepIndex > 0 && !DeploymentInProgress;
    public bool CanGoNext    => CurrentStep.CanProceed && !IsLastStep && !DeploymentInProgress;
    public bool IsLastStep   => CurrentStepIndex == _steps.Count - 1;
    public bool IsDeploymentReadyStep => CurrentStep is DeploymentSummaryViewModel;
    public double ProgressPercent => _steps.Count > 1
        ? (double)CurrentStepIndex / (_steps.Count - 1) * 100 : 0;

    public MainWindowViewModel(
        IProductInfoProvider         productInfo,
        IAboutDialogService          aboutDialog,
        WelcomeViewModel             welcome,
        ProductSystemHealthViewModel productHealth,
        VersionSelectionViewModel    versionSel,
        PathConfigViewModel          paths,
        DatabaseConfigViewModel      db,
        DomainConfigViewModel        domain,
        PatchManagementViewModel     patch,
        SecurityComplianceViewModel  security,
        PrerequisiteViewModel        prereq,
        DeploymentProgressViewModel    progress,
        DeploymentSummaryViewModel   summary)
    {
        _aboutDialog = aboutDialog;
        var snap = productInfo.GetSnapshot();
        AppVersion              = snap.DisplayVersion;
        ReleaseChannelDisplay   = snap.ReleaseChannel;

        welcome.StepIndex = 0;
        productHealth.StepIndex = 1;
        versionSel.StepIndex = 2;
        paths.StepIndex = 3;
        db.StepIndex = 4;
        domain.StepIndex = 5;
        patch.StepIndex = 6;
        security.StepIndex = 7;
        prereq.StepIndex = 8;
        summary.StepIndex = 9;
        progress.StepIndex = 10;

        _steps = new List<WizardStepViewModel>
        {
            welcome,
            productHealth,
            versionSel,
            paths,
            db,
            domain,
            patch,
            security,
            prereq,
            summary,
            progress,
        };

        Title      = "WebLogic Enterprise Deployment Manager";
        CurrentStepIndex = 0;
        _steps[0].IsActive = true;

        foreach (var step in _steps)
            step.PropertyChanged += OnStepPropertyChanged;
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WizardStepViewModel.CanProceed)
            or nameof(WizardStepViewModel.IsBusy)
            or nameof(WizardStepViewModel.IsValid))
        {
            RefreshNavigation();
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        var canLeave = await CurrentStep.OnNavigatingFromAsync();
        if (!canLeave) return;

        CurrentStep.ApplyToConfiguration(Configuration);
        CurrentStep.IsCompleted = true;
        CurrentStep.IsActive    = false;

        CurrentStepIndex++;
        CurrentStep.IsActive = true;
        OnPropertyChanged(nameof(IsDeploymentReadyStep));
        await CurrentStep.OnNavigatingToAsync(Configuration);

        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task BackAsync()
    {
        CurrentStep.IsActive = false;
        CurrentStepIndex--;
        CurrentStep.IsActive = true;
        OnPropertyChanged(nameof(IsDeploymentReadyStep));
        await CurrentStep.OnNavigatingToAsync(Configuration);

        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NavigateTo(int index)
    {
        if (index < 0 || index >= _steps.Count) return;
        // Only allow backward navigation to completed steps
        if (index < CurrentStepIndex && _steps[index].IsCompleted)
        {
            CurrentStep.IsActive    = false;
            CurrentStepIndex        = index;
            CurrentStep.IsActive    = true;
            OnPropertyChanged(nameof(IsDeploymentReadyStep));
            NextCommand.NotifyCanExecuteChanged();
            BackCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void ShowAbout() => _aboutDialog.ShowAbout();

    [RelayCommand]
    private static void OpenHelp()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://docs.oracle.com/en/middleware/fusion-middleware/weblogic-server/",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    /// <summary>Refresh Next/Back button states — called by child step VMs.</summary>
    public void RefreshNavigation()
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
    }
}
