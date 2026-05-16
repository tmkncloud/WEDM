using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// First wizard screen — selects Deploy New Environment vs Upgrade / Migrate Existing.
/// </summary>
public sealed partial class OperationSelectionViewModel : WizardStepViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    [NotifyPropertyChangedFor(nameof(IsDeploySelected))]
    [NotifyPropertyChangedFor(nameof(IsMigrateSelected))]
    [NotifyPropertyChangedFor(nameof(IsDecommissionSelected))]
    private WedmOperationMode _selectedOperation = WedmOperationMode.None;

    public bool IsDeploySelected  => SelectedOperation == WedmOperationMode.DeployNewEnvironment;
    public bool IsMigrateSelected => SelectedOperation == WedmOperationMode.UpgradeMigrateExisting;
    public bool IsDecommissionSelected => SelectedOperation == WedmOperationMode.DecommissionRemoveEnvironment;

    public override bool CanProceed => SelectedOperation != WedmOperationMode.None;

    public OperationSelectionViewModel()
    {
        StepTitle       = "Select Operation";
        StepDescription = "Deploy a new environment, upgrade/migrate existing middleware, or remove a WebLogic environment.";
        StepIcon        = "🔀";
    }

    [RelayCommand]
    private void SelectDeploy() => SelectedOperation = WedmOperationMode.DeployNewEnvironment;

    [RelayCommand]
    private void SelectMigrate() => SelectedOperation = WedmOperationMode.UpgradeMigrateExisting;

    [RelayCommand]
    private void SelectDecommission() => SelectedOperation = WedmOperationMode.DecommissionRemoveEnvironment;

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        // Operation mode is applied to MigrationConfiguration by MainWindowViewModel.
    }

    partial void OnSelectedOperationChanged(WedmOperationMode value)
    {
        OnPropertyChanged(nameof(IsDeploySelected));
        OnPropertyChanged(nameof(IsMigrateSelected));
        OnPropertyChanged(nameof(IsDecommissionSelected));
    }
}
