using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Migration;
using WEDM.Domain.Models;
using WEDM.UI.Services;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Migration;

public sealed partial class MigrationSummaryViewModel : MigrationWizardStepViewModel
{
    private readonly IConfigurationTransformationEngine _transformationEngine;
    private readonly IMigrationReportWriter _reportWriter;
    private readonly IMigrationSessionStore _sessionStore;
    private MigrationConfiguration? _sessionConfig;

    [ObservableProperty]
    private string _projectName = string.Empty;

    [ObservableProperty]
    private string _sourceSummary = string.Empty;

    [ObservableProperty]
    private string _targetSummary = string.Empty;

    [ObservableProperty]
    private string _upgradePath = string.Empty;

    [ObservableProperty]
    private string _strategyDisplay = string.Empty;

    [ObservableProperty]
    private double _readinessPercent;

    [ObservableProperty]
    private string _readinessLevelDisplay = string.Empty;

    [ObservableProperty]
    private string _complexityDisplay = string.Empty;

    [ObservableProperty]
    private string _effortDisplay = string.Empty;

    [ObservableProperty]
    private int _blockerCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _formCount;

    [ObservableProperty]
    private int _managedServerCount;

    [ObservableProperty]
    private string _executiveSummary = string.Empty;

    [ObservableProperty]
    private string _operationalRecommendations = string.Empty;

    [ObservableProperty]
    private string _transformationPlanPreview = string.Empty;

    [ObservableProperty]
    private string _lastReportPath = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _workspacePath = string.Empty;

    [ObservableProperty]
    private string _transformationConfidence = string.Empty;

    public override bool CanProceed => true;

    public MigrationSummaryViewModel(
        IConfigurationTransformationEngine transformationEngine,
        IMigrationReportWriter reportWriter,
        IMigrationSessionStore sessionStore)
    {
        _transformationEngine = transformationEngine;
        _reportWriter         = reportWriter;
        _sessionStore         = sessionStore;

        StepTitle       = "Migration Executive Summary";
        StepDescription = "Presentation-ready overview, assessment reports, and migration plan persistence.";
        StepIcon        = "📋";
    }

    public override async Task OnNavigatingToAsync(MigrationConfiguration config)
    {
        _sessionConfig = config;
        ProjectName          = config.Name;
        UpgradePath          = MigrationVersionMatrix.DescribeUpgradePath(config.Source.Release, config.Target.Release);
        SourceSummary        = $"{config.Source.DisplayName} · {config.Topology.DomainName} · {config.Topology.AdminServerUrl}";
        TargetSummary        = config.Target.DisplayName;
        StrategyDisplay      = FormatStrategy(config.Strategy);
        ReadinessPercent     = config.Readiness.ReadinessPercent;
        ReadinessLevelDisplay = config.Readiness.Level.ToString();
        ComplexityDisplay    = config.Readiness.Complexity.ToString();
        EffortDisplay        = config.Readiness.EffortCategory.ToString();
        BlockerCount         = config.Readiness.BlockerCount;
        WarningCount         = config.Readiness.WarningCount;
        FormCount            = config.FormsMetadata.FormCount;
        ManagedServerCount   = config.Topology.ManagedServerCount;
        ExecutiveSummary     = config.Readiness.ExecutiveSummary;
        OperationalRecommendations = BuildOperationalRecommendations(config);
        if (config.TransformationCompleted && config.Transformation is not null)
        {
            TransformationPlanPreview = config.Transformation.PlanPreview;
            WorkspacePath             = config.TransformationWorkspacePath ?? string.Empty;
            TransformationConfidence  = config.Transformation.Confidence.ToString();
        }
        else
        {
            TransformationPlanPreview = await _transformationEngine.BuildTransformationPlanPreviewAsync(config);
            WorkspacePath             = config.TransformationWorkspacePath ?? string.Empty;
            TransformationConfidence  = config.Transformation?.Confidence.ToString() ?? "Not assessed";
        }

        StatusMessage = config.TransformationCompleted
            ? "Migration artifacts ready — generate assessment reports or export the migration plan."
            : "Complete migration preparation or generate assessment reports for stakeholder review.";
    }

    [RelayCommand]
    private async Task GenerateReportsAsync()
    {
        if (_sessionConfig is null) return;

        try
        {
            SetBusy(true, "Generating migration assessment reports…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var dir = MigrationPaths.ReportsDirectory;
            var json = await _reportWriter.WriteJsonAsync(_sessionConfig, dir);
            var html = await _reportWriter.WriteHtmlAsync(_sessionConfig, dir);
            LastReportPath = dir;
            StatusMessage  = $"Reports generated: {Path.GetFileName(json)}, {Path.GetFileName(html)}";
            MigrationDiagnostics.TraceTiming("ReportGeneration", sw.ElapsedMilliseconds, dir);
        }
        catch (Exception ex)
        {
            HandleException(ex, "Report generation");
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private void OpenReportsFolder()
    {
        var dir = string.IsNullOrWhiteSpace(LastReportPath) ? MigrationPaths.ReportsDirectory : LastReportPath;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private async Task SaveSessionAsync()
    {
        if (_sessionConfig is null) return;

        var dlg = new SaveFileDialog
        {
            Title  = "Export migration plan",
            Filter = "WEDM migration plan (*.wedm-migration.json)|*.wedm-migration.json",
            FileName = $"{SanitizeFileName(_sessionConfig.Name)}.wedm-migration.json",
            InitialDirectory = MigrationPaths.SessionsDirectory,
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            SetBusy(true, "Saving migration plan…");
            Directory.CreateDirectory(Path.GetDirectoryName(dlg.FileName)!);
            await _sessionStore.SaveAsync(_sessionConfig, dlg.FileName);
            StatusMessage = $"Migration plan saved: {dlg.FileName}";
            MigrationDiagnostics.Trace("Session", $"Saved {dlg.FileName}");
        }
        catch (Exception ex)
        {
            HandleException(ex, "Save migration plan");
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private async Task LoadSessionAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Import migration plan",
            Filter = "WEDM migration plan (*.wedm-migration.json)|*.wedm-migration.json",
            InitialDirectory = MigrationPaths.SessionsDirectory,
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            SetBusy(true, "Loading migration plan…");
            var loaded = await _sessionStore.LoadAsync(dlg.FileName);
            if (_sessionConfig is not null)
            {
                CopyConfiguration(loaded, _sessionConfig);
                await OnNavigatingToAsync(_sessionConfig);
            }
            StatusMessage = $"Migration plan imported: {dlg.FileName}";
            MigrationDiagnostics.Trace("Session", $"Loaded {dlg.FileName}");
        }
        catch (Exception ex)
        {
            HandleException(ex, "Import migration plan");
        }
        finally
        {
            SetBusy(false);
        }
    }

    public override void ApplyToMigrationConfiguration(MigrationConfiguration config)
    {
        config.Name = ProjectName;
    }

    private static void CopyConfiguration(MigrationConfiguration source, MigrationConfiguration target)
    {
        target.Id                    = source.Id;
        target.Name                  = source.Name;
        target.OperationMode         = source.OperationMode;
        target.Source                = source.Source;
        target.Target                = source.Target;
        target.Strategy              = source.Strategy;
        target.Topology              = source.Topology;
        target.FormsMetadata         = source.FormsMetadata;
        target.Readiness             = source.Readiness;
        target.CompatibilityFindings = source.CompatibilityFindings.ToList();
        target.ValidationMessages    = source.ValidationMessages.ToList();
        target.DiscoveryInsights     = source.DiscoveryInsights.ToList();
        target.DiscoveryCompleted    = source.DiscoveryCompleted;
        target.AssessmentCompleted   = source.AssessmentCompleted;
        target.Notes                 = source.Notes;
        target.DiscoveryDurationMs   = source.DiscoveryDurationMs;
        target.AssessmentDurationMs  = source.AssessmentDurationMs;
        target.LastSavedUtc          = source.LastSavedUtc;
        target.OracleInventory       = source.OracleInventory;
        target.DomainAnalysis        = source.DomainAnalysis;
        target.DiscoveryStages       = source.DiscoveryStages.ToList();
        target.DiscoveryWarnings     = source.DiscoveryWarnings.ToList();
        target.DiscoveryUsedRealScan = source.DiscoveryUsedRealScan;
        target.Transformation              = source.Transformation;
        target.TransformationCompleted     = source.TransformationCompleted;
        target.TransformationDurationMs    = source.TransformationDurationMs;
        target.TransformationWorkspacePath = source.TransformationWorkspacePath;
        target.FormsModernization          = source.FormsModernization;
        target.ReportsModernization        = source.ReportsModernization;
        target.Execution                   = source.Execution;
        target.ExecutionCompleted          = source.ExecutionCompleted;
        target.ExecutionDurationMs         = source.ExecutionDurationMs;
    }

    private static string BuildOperationalRecommendations(MigrationConfiguration config)
    {
        var items = new List<string>();
        if (config.Readiness.BlockerCount > 0)
            items.Add("Resolve blocking compatibility items before scheduling production cutover.");
        if (config.FormsMetadata.UsesWebUtil)
            items.Add("Schedule WebUtil remediation and client deployment validation.");
        if (!config.Topology.NodeManagerConfigured)
            items.Add("Plan Node Manager enrollment as part of target domain provisioning.");
        if (config.Topology.JvmArguments.Any(a => a.Contains("PermGen", StringComparison.OrdinalIgnoreCase)))
            items.Add("Regenerate JVM startup arguments for the target JDK tier.");
        items.Add("Execute phased validation gates aligned with the selected migration strategy.");
        return string.Join(Environment.NewLine, items.Select(i => "• " + i));
    }

    private static string FormatStrategy(Domain.Enums.MigrationStrategyKind strategy) => strategy switch
    {
        Domain.Enums.MigrationStrategyKind.ParallelRun           => "Parallel run validation",
        Domain.Enums.MigrationStrategyKind.InPlaceUpgrade        => "In-place upgrade",
        Domain.Enums.MigrationStrategyKind.SideBySideCutover       => "Side-by-side cutover",
        Domain.Enums.MigrationStrategyKind.PhasedModuleMigration   => "Phased module migration",
        Domain.Enums.MigrationStrategyKind.LiftAndShiftReplatform  => "Lift-and-shift replatform",
        _ => strategy.ToString(),
    };

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }
}
