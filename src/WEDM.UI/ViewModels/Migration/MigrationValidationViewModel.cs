using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Migration;

public sealed partial class MigrationValidationViewModel : MigrationWizardStepViewModel
{
    [ObservableProperty]
    private bool _validationComplete;

    public ObservableCollection<MigrationValidationMessage> RiskMessages { get; } = [];

    public int CriticalCount => RiskMessages.Count(m => m.Severity == CompatibilitySeverity.Critical);
    public int HighCount     => RiskMessages.Count(m => m.Severity == CompatibilitySeverity.High);

    public override bool CanProceed => ValidationComplete;

    public MigrationValidationViewModel()
    {
        StepTitle       = "Validation & Risk Analysis";
        StepDescription = "Consolidate compatibility findings into an actionable risk register.";
        StepIcon        = "⚠";
    }

    [RelayCommand]
    private void RunValidation()
    {
        RiskMessages.Clear();
        // Populated from session on navigate; RunValidation refreshes display flags.
        ValidationComplete = RiskMessages.Count == 0 || CriticalCount == 0;
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(HighCount));
        OnPropertyChanged(nameof(CanProceed));
    }

    public override void ApplyToMigrationConfiguration(MigrationConfiguration config)
    {
        config.ValidationMessages = RiskMessages.ToList();
    }

    public override Task OnNavigatingToAsync(MigrationConfiguration config)
    {
        RiskMessages.Clear();

        foreach (var finding in config.CompatibilityFindings
                     .Where(f => f.Severity >= CompatibilitySeverity.Medium))
        {
            RiskMessages.Add(new MigrationValidationMessage
            {
                Severity = finding.Severity,
                Message  = finding.Title,
                Source   = finding.Category.ToString(),
            });
        }

        if (config.Topology.ManagedServerCount == 0 && config.DiscoveryCompleted)
        {
            RiskMessages.Add(new MigrationValidationMessage
            {
                Severity = CompatibilitySeverity.High,
                Message  = "No managed servers discovered — verify domain home path.",
                Source   = "Discovery",
            });
        }

        ValidationComplete = !config.CompatibilityFindings.Any(f => f.BlocksMigration);
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(HighCount));
        OnPropertyChanged(nameof(CanProceed));
        return Task.CompletedTask;
    }
}
