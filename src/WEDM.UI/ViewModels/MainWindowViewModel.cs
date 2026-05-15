using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.UI.Services;
using WEDM.UI.ViewModels.Base;
using WEDM.UI.ViewModels.Migration;
using WEDM.UI.ViewModels.Wizard;

namespace WEDM.UI.ViewModels;

/// <summary>
/// Root ViewModel — owns wizard step collections for deploy and migration workflows,
/// navigation state, and separate DeploymentConfiguration / MigrationConfiguration sessions.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly OperationSelectionViewModel _operationSelection;
    private readonly IReadOnlyList<WizardStepViewModel> _deploySteps;
    private readonly IReadOnlyList<WizardStepViewModel> _migrationSteps;
    private readonly ObservableCollection<WizardStepViewModel> _activeSteps = [];
    private readonly IAboutDialogService _aboutDialog;

    private WedmOperationMode _activeOperationMode = WedmOperationMode.None;
    private bool _workflowExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStep))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(IsDeploymentReadyStep))]
    [NotifyPropertyChangedFor(nameof(IsMigrationReadyStep))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsMigrationWorkflow))]
    [NotifyPropertyChangedFor(nameof(IsWorkflowExpanded))]
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

    public DeploymentConfiguration Configuration { get; } = new()
    {
        Name = "New WebLogic Deployment",
    };

    public MigrationConfiguration Migration { get; } = new()
    {
        Name = "Middleware Migration Project",
    };

    public WizardStepViewModel CurrentStep => _activeSteps[CurrentStepIndex];
    public IReadOnlyList<WizardStepViewModel> Steps => _activeSteps;
    public bool IsMigrationWorkflow => _activeOperationMode == WedmOperationMode.UpgradeMigrateExisting;

    public bool IsWorkflowExpanded => _workflowExpanded;

    public bool CanGoBack => CurrentStepIndex > 0 && !DeploymentInProgress;
    public bool CanGoNext => CurrentStep.CanProceed && !DeploymentInProgress
        && (!_workflowExpanded || CurrentStepIndex < _activeSteps.Count - 1);
    public bool IsLastStep => _workflowExpanded && CurrentStepIndex >= _activeSteps.Count - 1;
    public bool IsDeploymentReadyStep =>
        _activeOperationMode == WedmOperationMode.DeployNewEnvironment
        && CurrentStep is DeploymentSummaryViewModel;
    public bool IsMigrationReadyStep =>
        _activeOperationMode == WedmOperationMode.UpgradeMigrateExisting
        && CurrentStep is MigrationSummaryViewModel;

    public double ProgressPercent => _activeSteps.Count > 1
        ? (double)CurrentStepIndex / (_activeSteps.Count - 1) * 100
        : 0;

    public MainWindowViewModel(
        IProductInfoProvider              productInfo,
        IAboutDialogService               aboutDialog,
        OperationSelectionViewModel       operationSelection,
        WelcomeViewModel                  welcome,
        ProductSystemHealthViewModel      productHealth,
        VersionSelectionViewModel         versionSel,
        PathConfigViewModel               paths,
        DatabaseConfigViewModel           db,
        DomainConfigViewModel             domain,
        PatchManagementViewModel          patch,
        SecurityComplianceViewModel       security,
        PrerequisiteViewModel             prereq,
        DeploymentProgressViewModel       progress,
        DeploymentSummaryViewModel        summary,
        MigrationSourceVersionViewModel   migrationSource,
        MigrationTargetVersionViewModel   migrationTarget,
        MigrationDiscoveryViewModel       migrationDiscovery,
        MigrationCompatibilityViewModel   migrationCompatibility,
        MigrationStrategyViewModel        migrationStrategy,
        MigrationValidationViewModel      migrationValidation,
        MigrationTransformationViewModel  migrationTransformation,
        MigrationSummaryViewModel         migrationSummary,
        MigrationExecutionViewModel       migrationExecution)
    {
        _aboutDialog       = aboutDialog;
        _operationSelection = operationSelection;

        var snap = productInfo.GetSnapshot();
        AppVersion            = snap.DisplayVersion;
        ReleaseChannelDisplay = snap.ReleaseChannel;

        operationSelection.StepIndex = 0;
        welcome.StepIndex            = 1;
        productHealth.StepIndex      = 2;
        versionSel.StepIndex         = 3;
        paths.StepIndex              = 4;
        db.StepIndex                 = 5;
        domain.StepIndex             = 6;
        patch.StepIndex              = 7;
        security.StepIndex           = 8;
        prereq.StepIndex             = 9;
        summary.StepIndex            = 10;
        progress.StepIndex           = 11;

        migrationSource.StepIndex       = 1;
        migrationTarget.StepIndex       = 2;
        migrationDiscovery.StepIndex    = 3;
        migrationCompatibility.StepIndex = 4;
        migrationStrategy.StepIndex     = 5;
        migrationValidation.StepIndex      = 6;
        migrationTransformation.StepIndex  = 7;
        migrationSummary.StepIndex         = 8;
        migrationExecution.StepIndex       = 9;

        _deploySteps = new List<WizardStepViewModel>
        {
            operationSelection,
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

        _migrationSteps = new List<WizardStepViewModel>
        {
            operationSelection,
            migrationSource,
            migrationTarget,
            migrationDiscovery,
            migrationCompatibility,
            migrationStrategy,
            migrationValidation,
            migrationTransformation,
            migrationSummary,
            migrationExecution,
        };

        Title = "WebLogic Enterprise Deployment Manager";
        ResetToOperationSelection();

        SubscribeToSteps(_deploySteps);
        SubscribeToSteps(_migrationSteps);

        _operationSelection.PropertyChanged += OnOperationSelectionPropertyChanged;
    }

    private async void OnOperationSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(OperationSelectionViewModel.SelectedOperation) or "SelectedOperation"))
            return;

        if (_workflowExpanded || _operationSelection.SelectedOperation == WedmOperationMode.None)
            return;

        if (CurrentStep is not OperationSelectionViewModel)
            return;

        await ActivateSelectedWorkflowAsync();
    }

    private void SubscribeToSteps(IEnumerable<WizardStepViewModel> steps)
    {
        foreach (var step in steps)
            step.PropertyChanged += OnStepPropertyChanged;
    }

    private void ResetToOperationSelection()
    {
        _workflowExpanded     = false;
        _activeOperationMode  = WedmOperationMode.None;
        _activeSteps.Clear();
        _activeSteps.Add(_operationSelection);
        CurrentStepIndex = 0;

        foreach (var step in _deploySteps.Concat(_migrationSteps).Distinct())
        {
            step.IsActive    = false;
            step.IsCompleted = false;
        }

        _operationSelection.IsActive = true;
        NotifyWorkflowStructureChanged();
    }

    private async Task ActivateSelectedWorkflowAsync()
    {
        var mode = _operationSelection.SelectedOperation;
        if (mode == WedmOperationMode.None || _workflowExpanded)
            return;

        if (!await _operationSelection.OnNavigatingFromAsync())
            return;

        ApplyCurrentStep();
        ExpandWorkflow(mode);
        await EnterCurrentStepAsync();
        NotifyWorkflowStructureChanged();
    }

    private void ExpandWorkflow(WedmOperationMode mode)
    {
        _activeOperationMode = mode;
        Migration.OperationMode = mode;

        var sourceSteps = mode == WedmOperationMode.DeployNewEnvironment
            ? _deploySteps
            : _migrationSteps;

        _activeSteps.Clear();
        foreach (var step in sourceSteps)
            _activeSteps.Add(step);

        _workflowExpanded = true;
        _operationSelection.IsCompleted = true;
        _operationSelection.IsActive    = false;

        CurrentStepIndex = 1;
        CurrentStep.IsActive = true;

        NotifyWorkflowStructureChanged();
    }

    private void ApplyCurrentStep()
    {
        if (!CurrentStep.CanProceed)
            return;

        if (CurrentStep is MigrationWizardStepViewModel migrationStep)
            migrationStep.ApplyToMigrationConfiguration(Migration);
        else
            CurrentStep.ApplyToConfiguration(Configuration);

        if (CurrentStep is OperationSelectionViewModel op)
        {
            Migration.OperationMode = op.SelectedOperation;
            Configuration.Name = op.SelectedOperation == WedmOperationMode.DeployNewEnvironment
                ? "New WebLogic Deployment"
                : Configuration.Name;
        }
    }

    private async Task EnterCurrentStepAsync()
    {
        if (CurrentStep is MigrationWizardStepViewModel migrationStep)
            await migrationStep.OnNavigatingToAsync(Migration);
        else
            await CurrentStep.OnNavigatingToAsync(Configuration);
    }

    private void NotifyNavigationStateChanged() => NotifyWorkflowStructureChanged();

    private void NotifyWorkflowStructureChanged()
    {
        OnPropertyChanged(nameof(Steps));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(IsWorkflowExpanded));
        OnPropertyChanged(nameof(IsMigrationWorkflow));
        OnPropertyChanged(nameof(IsDeploymentReadyStep));
        OnPropertyChanged(nameof(IsMigrationReadyStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(ProgressPercent));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is WizardStepViewModel step
            && e.PropertyName == nameof(ViewModelBase.IsBusy))
        {
            step.NotifyValidationStateChanged();
        }

        if (e.PropertyName is nameof(WizardStepViewModel.CanProceed)
            or nameof(ViewModelBase.IsBusy)
            or nameof(WizardStepViewModel.IsValid)
            or nameof(WizardStepViewModel.HasStepValidationError))
        {
            RefreshNavigation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        var canLeave = await CurrentStep.OnNavigatingFromAsync();
        if (!canLeave) return;

        ApplyCurrentStep();

        if (!_workflowExpanded && CurrentStep is OperationSelectionViewModel op
            && op.SelectedOperation != WedmOperationMode.None)
        {
            ExpandWorkflow(op.SelectedOperation);
            await EnterCurrentStepAsync();
            NotifyWorkflowStructureChanged();
            return;
        }

        var fromTitle = CurrentStep.StepTitle;
        CurrentStep.IsCompleted = true;
        CurrentStep.IsActive    = false;

        CurrentStepIndex++;
        CurrentStep.IsActive = true;
        LogWorkflowTransition(fromTitle, CurrentStep.StepTitle);

        await EnterCurrentStepAsync();
        NotifyNavigationStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task BackAsync()
    {
        if (CurrentStepIndex == 1 && _workflowExpanded)
        {
            ApplyCurrentStep();
            CurrentStep.IsActive = false;
            ResetToOperationSelection();
            await _operationSelection.OnNavigatingToAsync(Configuration);
            NotifyNavigationStateChanged();
            return;
        }

        var fromTitle = CurrentStep.StepTitle;
        CurrentStep.IsActive = false;
        CurrentStepIndex--;
        CurrentStep.IsActive = true;
        LogWorkflowTransition(fromTitle, CurrentStep.StepTitle);

        await EnterCurrentStepAsync();
        NotifyNavigationStateChanged();
    }

    [RelayCommand]
    private async Task NavigateTo(int index)
    {
        if (index < 0 || index >= _activeSteps.Count) return;
        if (DeploymentInProgress) return;

        if (index == CurrentStepIndex)
        {
            await EnterCurrentStepAsync();
            return;
        }

        if (!_workflowExpanded && index > 0) return;

        CurrentStep.IsActive = false;

        if (index > CurrentStepIndex)
        {
            var canLeave = await CurrentStep.OnNavigatingFromAsync();
            if (!canLeave)
            {
                CurrentStep.IsActive = true;
                return;
            }

            ApplyCurrentStep();
            CurrentStep.IsCompleted = true;

            var from = CurrentStepIndex;
            for (var i = from + 1; i < index; i++)
            {
                var step = _activeSteps[i];
                step.ValidateStep();
                if (!step.CanProceed)
                {
                    foreach (var s in _activeSteps)
                        s.IsActive = false;
                    CurrentStepIndex = i;
                    CurrentStep.IsActive = true;
                    NotifyNavigationStateChanged();
                    return;
                }

                if (step is MigrationWizardStepViewModel skippedMigration)
                {
                    await skippedMigration.OnNavigatingToAsync(Migration);
                    skippedMigration.ApplyToMigrationConfiguration(Migration);
                }
                else
                {
                    await step.OnNavigatingToAsync(Configuration);
                    step.ApplyToConfiguration(Configuration);
                }

                step.IsCompleted = true;
            }

            CurrentStepIndex = index;
            CurrentStep.IsActive = true;
        }
        else
        {
            CurrentStepIndex = index;
            CurrentStep.IsActive = true;
        }

        await EnterCurrentStepAsync();
        NotifyNavigationStateChanged();
    }

    [RelayCommand]
    private void ShowAbout() => _aboutDialog.ShowAbout();

    [RelayCommand]
    private static void OpenHelp()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://docs.oracle.com/en/middleware/fusion-middleware/weblogic-server/",
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    public void RefreshNavigation() => NotifyNavigationStateChanged();

    private void LogWorkflowTransition(string fromStep, string toStep)
    {
        if (_activeOperationMode == WedmOperationMode.UpgradeMigrateExisting)
            MigrationDiagnostics.TraceWorkflowPhase(fromStep, toStep, "Migration");
    }
}
