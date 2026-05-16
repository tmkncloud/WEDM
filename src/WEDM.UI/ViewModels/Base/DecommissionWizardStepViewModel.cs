using WEDM.Domain.Models;

namespace WEDM.UI.ViewModels.Base;

public abstract partial class DecommissionWizardStepViewModel : WizardStepViewModel
{
    public override void ApplyToConfiguration(DeploymentConfiguration config) { }

    public abstract void ApplyToDecommissionConfiguration(DecommissionConfiguration config);

    public virtual Task OnNavigatingToAsync(DecommissionConfiguration config)
        => Task.CompletedTask;
}
