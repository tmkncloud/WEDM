using CommunityToolkit.Mvvm.ComponentModel;
using WEDM.Domain.Enums;
using WEDM.Domain.Migration;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Migration;

public sealed partial class MigrationSourceVersionViewModel : MigrationWizardStepViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    [NotifyPropertyChangedFor(nameof(SelectedSourceDisplay))]
    [NotifyPropertyChangedFor(nameof(SelectedSourceSummary))]
    private MiddlewareReleaseKind _selectedSource = MiddlewareReleaseKind.Forms11g;

    public string SelectedSourceDisplay => MigrationVersionMatrix.GetDisplayName(SelectedSource);

    public string SelectedSourceSummary => $"Selected: {SelectedSourceDisplay}";

    public IReadOnlyList<MiddlewareReleaseKind> SourceOptions { get; }
        = MigrationVersionMatrix.GetSupportedSources();

    public override bool CanProceed => SelectedSource != MiddlewareReleaseKind.Unknown;

    public MigrationSourceVersionViewModel()
    {
        StepTitle       = "Source Version";
        StepDescription = "Select the Oracle Forms / middleware release currently running in your environment.";
        StepIcon        = "📥";
    }

    public override void ApplyToMigrationConfiguration(MigrationConfiguration config)
    {
        config.Source.Release     = SelectedSource;
        config.Source.DisplayName = MigrationVersionMatrix.GetDisplayName(SelectedSource);
    }

    public override Task OnNavigatingToAsync(MigrationConfiguration config)
    {
        if (config.Source.Release != MiddlewareReleaseKind.Unknown)
            SelectedSource = config.Source.Release;
        return Task.CompletedTask;
    }
}
