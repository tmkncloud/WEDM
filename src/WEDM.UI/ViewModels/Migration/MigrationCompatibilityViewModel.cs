using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Migration;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Migration;

public sealed partial class MigrationCompatibilityViewModel : MigrationWizardStepViewModel
{
    private readonly ICompatibilityAssessmentEngine _assessmentEngine;
    private MigrationConfiguration? _sessionConfig;

    [ObservableProperty]
    private bool _assessmentRunning;

    [ObservableProperty]
    private double _readinessPercent;

    [ObservableProperty]
    private int _readinessScore;

    [ObservableProperty]
    private MigrationReadinessLevel _readinessLevel = MigrationReadinessLevel.NotAssessed;

    [ObservableProperty]
    private MigrationComplexityKind _complexity = MigrationComplexityKind.Medium;

    [ObservableProperty]
    private MigrationEffortCategory _effortCategory = MigrationEffortCategory.Standard;

    [ObservableProperty]
    private string _readinessSummary = "Run compatibility analysis to generate migration readiness scoring.";

    [ObservableProperty]
    private string _executiveSummary = string.Empty;

    [ObservableProperty]
    private string _technicalSummary = string.Empty;

    [ObservableProperty]
    private string _upgradePathVisual = string.Empty;

    [ObservableProperty]
    private int _blockerCount;

    [ObservableProperty]
    private int _criticalCount;

    [ObservableProperty]
    private int _highCount;

    [ObservableProperty]
    private int _mediumCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private string _statusBannerKind = "Info";

    public ObservableCollection<CompatibilityFinding> Findings { get; } = [];

    public bool HasAssessment => ReadinessLevel != MigrationReadinessLevel.NotAssessed;
    public bool IsEmptyState => !HasAssessment && !AssessmentRunning;

    public override bool CanProceed =>
        HasAssessment
        && ReadinessLevel is MigrationReadinessLevel.Ready
            or MigrationReadinessLevel.ReadyWithRemediation
            or MigrationReadinessLevel.ModerateRisk
        && !AssessmentRunning;

    public MigrationCompatibilityViewModel(ICompatibilityAssessmentEngine assessmentEngine)
    {
        _assessmentEngine = assessmentEngine;
        StepTitle       = "Compatibility Assessment";
        StepDescription = "Weighted risk analysis, readiness scoring, and modernization complexity classification.";
        StepIcon        = "📊";
    }

    [RelayCommand]
    private async Task RunAssessmentAsync()
    {
        if (_sessionConfig is null) return;

        var sw = Stopwatch.StartNew();
        try
        {
            AssessmentRunning = true;
            IsBusy            = true;
            ClearError();
            SetBusy(true, "Running compatibility analysis…");
            StatusBannerKind = "Progress";

            var snapshot = await _assessmentEngine.AssessAsync(_sessionConfig);
            ApplySnapshot(snapshot);

            Findings.Clear();
            foreach (var finding in _assessmentEngine.GetLastFindings())
                Findings.Add(finding);

            StatusBannerKind = BlockerCount > 0 ? "Warning" : "Success";
            MigrationDiagnostics.TraceTiming("Assessment", sw.ElapsedMilliseconds,
                $"readiness={ReadinessPercent:F1}%");
            MigrationDiagnostics.TraceAssessmentSummary(new MigrationReadinessLogSummary(
                ReadinessPercent, Complexity.ToString(), BlockerCount, WarningCount));

            OnPropertyChanged(nameof(CanProceed));
            OnPropertyChanged(nameof(HasAssessment));
            OnPropertyChanged(nameof(IsEmptyState));
        }
        catch (Exception ex)
        {
            StatusBannerKind = "Error";
            HandleException(ex, "Compatibility analysis");
        }
        finally
        {
            AssessmentRunning = false;
            IsBusy            = false;
            SetBusy(false);
        }
    }

    private void ApplySnapshot(MigrationReadinessSnapshot snapshot)
    {
        ReadinessScore     = snapshot.Score;
        ReadinessPercent   = snapshot.ReadinessPercent;
        ReadinessLevel     = snapshot.Level;
        ReadinessSummary   = snapshot.Summary;
        ExecutiveSummary   = snapshot.ExecutiveSummary;
        TechnicalSummary   = snapshot.TechnicalSummary;
        Complexity         = snapshot.Complexity;
        EffortCategory     = snapshot.EffortCategory;
        BlockerCount       = snapshot.BlockerCount;
        CriticalCount      = snapshot.CriticalCount;
        HighCount          = snapshot.HighCount;
        MediumCount        = snapshot.MediumCount;
        WarningCount       = snapshot.WarningCount;
    }

    public override void ApplyToMigrationConfiguration(MigrationConfiguration config)
    {
        config.Readiness             = _sessionConfig?.Readiness ?? config.Readiness;
        config.CompatibilityFindings = Findings.ToList();
        config.AssessmentCompleted   = HasAssessment;
    }

    public override Task OnNavigatingToAsync(MigrationConfiguration config)
    {
        _sessionConfig = config;
        UpgradePathVisual = MigrationVersionMatrix.DescribeUpgradePath(
            config.Source.Release, config.Target.Release);

        if (config.AssessmentCompleted)
        {
            ApplySnapshot(config.Readiness);
            Findings.Clear();
            foreach (var f in config.CompatibilityFindings)
                Findings.Add(f);
            StatusBannerKind = "Success";
        }

        OnPropertyChanged(nameof(HasAssessment));
        OnPropertyChanged(nameof(IsEmptyState));
        return Task.CompletedTask;
    }
}
