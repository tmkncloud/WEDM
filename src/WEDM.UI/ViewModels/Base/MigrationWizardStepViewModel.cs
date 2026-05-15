using WEDM.Domain.Models;

namespace WEDM.UI.ViewModels.Base;

/// <summary>
/// Base for migration workflow wizard steps.
/// Deployment steps use <see cref="WizardStepViewModel"/>; migration steps use this type
/// so configuration is kept on <see cref="MigrationConfiguration"/> only.
/// </summary>
public abstract partial class MigrationWizardStepViewModel : WizardStepViewModel
{
    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        // Migration steps do not mutate deployment configuration.
    }

    public abstract void ApplyToMigrationConfiguration(MigrationConfiguration config);

    public virtual Task OnNavigatingToAsync(MigrationConfiguration config)
        => Task.CompletedTask;
}
