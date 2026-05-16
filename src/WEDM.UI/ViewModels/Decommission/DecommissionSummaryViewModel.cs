using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Decommission;

public sealed class DecommissionSummaryViewModel : DecommissionWizardStepViewModel
{
    public DecommissionSummaryViewModel()
    {
        StepTitle       = "Confirm Decommission";
        StepDescription = "Review the removal plan and start environment decommission.";
        StepIcon        = "📋";
    }

    public override bool CanProceed => true;

    public override void ApplyToDecommissionConfiguration(DecommissionConfiguration config) { }
}
