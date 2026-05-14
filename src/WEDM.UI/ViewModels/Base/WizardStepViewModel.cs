using CommunityToolkit.Mvvm.ComponentModel;

namespace WEDM.UI.ViewModels.Base;

/// <summary>
/// Base class for all wizard step view models.
/// Adds step metadata, validation state, and navigation signals
/// consumed by the MainWindowViewModel wizard controller.
///
/// Navigation lifecycle:
///   OnNavigatingTo()  → called when this step becomes active
///   OnNavigatingFrom() → called before navigation away (return false to block)
///   CanProceed        → controls the Next button enable state
/// </summary>
public abstract partial class WizardStepViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _stepTitle = string.Empty;

    [ObservableProperty]
    private string _stepDescription = string.Empty;

    [ObservableProperty]
    private string _stepIcon = "⚙";   // Unicode icon for sidebar

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private bool _isValid = true;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _isActive;

    public int StepIndex { get; set; }

    /// <summary>
    /// Whether the wizard can proceed from this step.
    /// Override to add field-level validation logic.
    /// </summary>
    public virtual bool CanProceed => IsValid && !IsBusy;

    /// <summary>Called when the wizard navigates TO this step.</summary>
    public virtual Task OnNavigatingToAsync() => Task.CompletedTask;

    /// <summary>
    /// Called when the wizard navigates TO this step with the shared deployment configuration.
    /// Steps that need a full configuration snapshot (validation, summary, deployment)
    /// override this method; the parameterless overload is kept for simple steps.
    /// </summary>
    public virtual Task OnNavigatingToAsync(Domain.Models.DeploymentConfiguration config)
        => OnNavigatingToAsync();

    /// <summary>
    /// Called when the wizard attempts to navigate FROM this step.
    /// Return false to block navigation (e.g., failed validation).
    /// </summary>
    public virtual Task<bool> OnNavigatingFromAsync() => Task.FromResult(true);

    /// <summary>Write current values into the shared DeploymentConfiguration.</summary>
    public abstract void ApplyToConfiguration(Domain.Models.DeploymentConfiguration config);
}
