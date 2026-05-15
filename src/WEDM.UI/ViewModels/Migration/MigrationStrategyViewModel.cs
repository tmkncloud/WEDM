using CommunityToolkit.Mvvm.ComponentModel;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Migration;

public sealed partial class MigrationStrategyViewModel : MigrationWizardStepViewModel
{
    [ObservableProperty]
    private MigrationStrategyKind _selectedStrategy = MigrationStrategyKind.PhasedModuleMigration;

    [ObservableProperty]
    private bool _enableParallelRunWindow;

    [ObservableProperty]
    private bool _generateRollbackSnapshot = true;

    [ObservableProperty]
    private bool _scheduleValidationGates = true;

    [ObservableProperty]
    private string _strategyNotes = string.Empty;

    public IReadOnlyList<MigrationStrategyKind> StrategyOptions { get; }
        = Enum.GetValues<MigrationStrategyKind>();

    public string StrategyDescription => SelectedStrategy switch
    {
        MigrationStrategyKind.ParallelRun =>
            "Run source and target environments in parallel during validation; cut over when sign-off is complete.",
        MigrationStrategyKind.InPlaceUpgrade =>
            "Upgrade middleware in place on existing hosts — shortest timeline, highest operational risk.",
        MigrationStrategyKind.SideBySideCutover =>
            "Provision a new target stack and switch traffic at a defined cutover window.",
        MigrationStrategyKind.PhasedModuleMigration =>
            "Migrate Forms modules in phases with automated validation gates between waves.",
        MigrationStrategyKind.LiftAndShiftReplatform =>
            "Replatform to new hardware/OS while preserving application configuration patterns.",
        _ => string.Empty,
    };

    public override bool CanProceed => true;

    public MigrationStrategyViewModel()
    {
        StepTitle       = "Migration Strategy";
        StepDescription = "Define cutover approach, rollback posture, and validation gates.";
        StepIcon        = "🗺";
    }

    partial void OnSelectedStrategyChanged(MigrationStrategyKind value)
        => OnPropertyChanged(nameof(StrategyDescription));

    public override void ApplyToMigrationConfiguration(MigrationConfiguration config)
    {
        config.Strategy = SelectedStrategy;
        config.Notes    = StrategyNotes;
    }

    public override Task OnNavigatingToAsync(MigrationConfiguration config)
    {
        SelectedStrategy = config.Strategy;
        StrategyNotes    = config.Notes ?? string.Empty;
        return Task.CompletedTask;
    }
}
