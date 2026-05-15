using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using WEDM.Domain.Enums;
using WEDM.Domain.Migration;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Migration;

public sealed record MigrationVersionOption(MiddlewareReleaseKind Release, string ShortLabel, string DisplayName);

public sealed partial class MigrationTargetVersionViewModel : MigrationWizardStepViewModel
{
    private MigrationConfiguration? _sessionConfig;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    [NotifyPropertyChangedFor(nameof(UpgradePathDescription))]
    [NotifyPropertyChangedFor(nameof(HasValidTargets))]
    private MiddlewareReleaseKind _selectedTarget = MiddlewareReleaseKind.Unknown;

    [ObservableProperty]
    private MigrationVersionOption? _selectedTargetOption;

    public ObservableCollection<MigrationVersionOption> TargetOptions { get; } = [];

    public bool HasValidTargets => TargetOptions.Count > 0;

    public string UpgradePathDescription =>
        _sessionConfig is null || SelectedTarget == MiddlewareReleaseKind.Unknown
            ? "Select a target release."
            : MigrationVersionMatrix.DescribeUpgradePath(_sessionConfig.Source.Release, SelectedTarget);

    public override bool CanProceed =>
        SelectedTarget != MiddlewareReleaseKind.Unknown
        && _sessionConfig is not null
        && MigrationVersionMatrix.IsValidUpgradePath(_sessionConfig.Source.Release, SelectedTarget);

    public MigrationTargetVersionViewModel()
    {
        StepTitle       = "Target Version";
        StepDescription = "Choose the supported upgrade destination for your source environment.";
        StepIcon        = "🎯";
    }

    public override void ApplyToMigrationConfiguration(MigrationConfiguration config)
    {
        config.Target.Release     = SelectedTarget;
        config.Target.DisplayName = MigrationVersionMatrix.GetDisplayName(SelectedTarget);
    }

    public override Task OnNavigatingToAsync(MigrationConfiguration config)
    {
        _sessionConfig = config;
        RefreshTargetOptions(config.Source.Release);

        if (config.Target.Release != MiddlewareReleaseKind.Unknown
            && MigrationVersionMatrix.IsValidUpgradePath(config.Source.Release, config.Target.Release))
        {
            SelectedTarget       = config.Target.Release;
            SelectedTargetOption = TargetOptions.FirstOrDefault(o => o.Release == config.Target.Release);
        }
        else if (TargetOptions.Count > 0)
        {
            SelectedTargetOption = TargetOptions[^1];
            SelectedTarget       = SelectedTargetOption.Release;
        }
        else
        {
            SelectedTarget       = MiddlewareReleaseKind.Unknown;
            SelectedTargetOption = null;
        }

        return Task.CompletedTask;
    }

    public void RefreshTargetOptions(MiddlewareReleaseKind source)
    {
        TargetOptions.Clear();
        foreach (var target in MigrationVersionMatrix.GetAllowedTargets(source))
        {
            TargetOptions.Add(new MigrationVersionOption(
                target,
                MigrationVersionMatrix.GetShortLabel(target),
                MigrationVersionMatrix.GetDisplayName(target)));
        }

        OnPropertyChanged(nameof(HasValidTargets));
        OnPropertyChanged(nameof(UpgradePathDescription));
        OnPropertyChanged(nameof(CanProceed));
    }

    partial void OnSelectedTargetChanged(MiddlewareReleaseKind value)
    {
        SelectedTargetOption = TargetOptions.FirstOrDefault(o => o.Release == value);
        OnPropertyChanged(nameof(UpgradePathDescription));
        OnPropertyChanged(nameof(CanProceed));
    }

    partial void OnSelectedTargetOptionChanged(MigrationVersionOption? value)
    {
        if (value is not null && value.Release != SelectedTarget)
            SelectedTarget = value.Release;
    }
}
