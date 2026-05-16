using CommunityToolkit.Mvvm.ComponentModel;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Decommission;

public sealed partial class DecommissionPreviewViewModel : DecommissionWizardStepViewModel
{
    [ObservableProperty] private bool _dryRun = true;
    [ObservableProperty] private bool _aggressiveCleanup;
    [ObservableProperty] private bool _removeSnapshots;
    [ObservableProperty] private string _confirmationInput = string.Empty;

    public DecommissionPreviewViewModel()
    {
        StepTitle       = "Removal Preview";
        StepDescription = "Review cleanup options. Type DECOMMISSION to confirm destructive removal.";
        StepIcon        = "⚠";
    }

    public override bool CanProceed =>
        string.Equals(ConfirmationInput.Trim(), "DECOMMISSION", StringComparison.OrdinalIgnoreCase);

    public override void ApplyToDecommissionConfiguration(DecommissionConfiguration config)
    {
        config.Options.DryRun             = DryRun;
        config.Options.AggressiveCleanup    = AggressiveCleanup;
        config.Options.RemoveSnapshots      = RemoveSnapshots;
        config.Options.ConfirmationPhrase   = ConfirmationInput.Trim();
    }
}
