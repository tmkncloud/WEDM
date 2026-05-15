using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.UI.Services;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Migration;

public sealed partial class MigrationTransformationViewModel : MigrationWizardStepViewModel
{
    private readonly ITransformationOrchestrator _orchestrator;
    private MigrationConfiguration? _sessionConfig;

    [ObservableProperty] private bool _transformInProgress;
    [ObservableProperty] private double _transformProgress;
    [ObservableProperty] private string _statusMessage = "Generate migration artifacts to prepare WLST scripts, transformed configs, and enterprise migration plan.";
    [ObservableProperty] private string _workspacePath = string.Empty;
    [ObservableProperty] private string _confidenceDisplay = "Not assessed";
    [ObservableProperty] private int _artifactCount;
    [ObservableProperty] private int _remediationCount;
    [ObservableProperty] private string _selectedArtifactPreview = string.Empty;
    [ObservableProperty] private string _planPreview = string.Empty;
    [ObservableProperty] private string _formsModernizationSummary = string.Empty;
    [ObservableProperty] private string _reportsModernizationSummary = string.Empty;

    public ObservableCollection<TransformationStageResult> TransformationStages { get; } = [];
    public ObservableCollection<GeneratedTransformationArtifact> Artifacts { get; } = [];
    public ObservableCollection<RemediationRecommendation> Remediations { get; } = [];
    public ObservableCollection<TransformationValidationMessage> ValidationMessages { get; } = [];
    public ObservableCollection<ConfigTransformationRecord> ConfigDiffs { get; } = [];

    public bool HasTransformation => _sessionConfig?.TransformationCompleted == true;
    public bool IsEmptyState => !HasTransformation && !TransformInProgress;

    public override bool CanProceed => HasTransformation && !TransformInProgress;

    public MigrationTransformationViewModel(ITransformationOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        _orchestrator.ProgressChanged += OnProgress;

        StepTitle       = "Migration Preparation";
        StepDescription = "Generate WLST scripts, transformed configs, remediation plans, and migration workspace artifacts.";
        StepIcon        = "⚙️";
    }

    private void OnProgress(object? sender, TransformationProgressEventArgs e)
    {
        TransformProgress = e.OverallPercent;
        StatusMessage     = $"{e.Stage.DisplayName}: {e.Stage.Status}";

        var existing = TransformationStages.FirstOrDefault(s => s.Stage == e.Stage.Stage);
        if (existing is not null)
            TransformationStages[TransformationStages.IndexOf(existing)] = e.Stage;
        else
            TransformationStages.Add(e.Stage);
    }

    [RelayCommand]
    private async Task RunTransformationAsync()
    {
        if (_sessionConfig is null) return;

        var sw = Stopwatch.StartNew();
        try
        {
            TransformInProgress = true;
            IsBusy              = true;
            ClearError();
            TransformationStages.Clear();
            Artifacts.Clear();
            SetBusy(true, "Running transformation pipeline…");

            var result = await _orchestrator.ExecuteAsync(_sessionConfig, new TransformationExecutionOptions
            {
                WorkspaceRoot = MigrationPaths.WorkspacesDirectory,
            });

            ApplyResult(result);
            _sessionConfig.Transformation              = result;
            _sessionConfig.TransformationCompleted     = result.Completed;
            _sessionConfig.TransformationDurationMs    = result.TotalDurationMs;
            _sessionConfig.TransformationWorkspacePath = result.WorkspacePath;
            _sessionConfig.FormsModernization          = result.FormsModernization;
            _sessionConfig.ReportsModernization        = result.ReportsModernization;

            StatusMessage = result.Completed
                ? $"Transformation complete — {result.Artifacts.Count} artifacts in workspace ({result.TotalDurationMs} ms)."
                : $"Transformation finished with warnings — review validation summary ({result.TotalDurationMs} ms).";

            TransformationDiagnostics.TraceArtifacts(result.WorkspacePath, result.Artifacts.Count, result.Confidence.ToString());
            TransformationDiagnostics.TraceTiming("Transformation", sw.ElapsedMilliseconds);
            foreach (var stage in result.Stages)
                TransformationDiagnostics.TraceStage(stage.DisplayName, stage.Status.ToString(), stage.DurationMs);
        }
        catch (Exception ex)
        {
            StatusMessage = "Transformation could not complete. Review diagnostics and retry.";
            HandleException(ex, "Transformation");
        }
        finally
        {
            TransformInProgress = false;
            IsBusy              = false;
            SetBusy(false);
            TransformProgress = HasTransformation ? 100 : 0;
            NotifyProceedState();
        }
    }

    [RelayCommand]
    private void OpenWorkspace()
    {
        if (string.IsNullOrWhiteSpace(WorkspacePath)) return;
        Directory.CreateDirectory(WorkspacePath);
        Process.Start(new ProcessStartInfo { FileName = WorkspacePath, UseShellExecute = true });
    }

    [RelayCommand]
    private void PreviewArtifact(GeneratedTransformationArtifact? artifact)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(WorkspacePath)) return;
        var path = Path.Combine(WorkspacePath, artifact.RelativePath);
        if (!File.Exists(path))
        {
            SelectedArtifactPreview = $"Artifact not found: {path}";
            return;
        }

        try
        {
            var text = File.ReadAllText(path);
            SelectedArtifactPreview = text.Length > 12000 ? text[..12000] + "\n... [truncated]" : text;
        }
        catch (Exception ex)
        {
            SelectedArtifactPreview = ex.Message;
        }
    }

    private void ApplyResult(TransformationExecutionResult result)
    {
        WorkspacePath    = result.WorkspacePath;
        ConfidenceDisplay = result.Confidence.ToString();
        ArtifactCount    = result.Artifacts.Count;
        RemediationCount = result.Remediations.Count;
        PlanPreview      = result.PlanPreview;

        FormsModernizationSummary =
            $"Complexity {result.FormsModernization.ComplexityScore} · {result.FormsModernization.Blockers.Count} blockers · WebUtil: {result.FormsModernization.WebUtilClassification}";
        ReportsModernizationSummary = result.ReportsModernization.ReadinessSummary;

        Artifacts.Clear();
        foreach (var a in result.Artifacts)
            Artifacts.Add(a);

        Remediations.Clear();
        foreach (var r in result.Remediations.Take(50))
            Remediations.Add(r);

        ValidationMessages.Clear();
        foreach (var m in result.Validation.Messages)
            ValidationMessages.Add(m);

        ConfigDiffs.Clear();
        foreach (var c in result.ConfigTransformations)
            ConfigDiffs.Add(c);

        foreach (var stage in result.Stages)
        {
            if (!TransformationStages.Any(s => s.Stage == stage.Stage))
                TransformationStages.Add(stage);
        }

        if (Artifacts.Count > 0)
            PreviewArtifact(Artifacts[0]);
    }

    public override Task OnNavigatingToAsync(MigrationConfiguration config)
    {
        _sessionConfig = config;
        if (config.TransformationCompleted && config.Transformation is not null)
            ApplyResult(config.Transformation);

        StatusMessage = config.TransformationCompleted
            ? $"Transformation artifacts loaded from {config.TransformationWorkspacePath}. Re-run to regenerate."
            : "Generate migration artifacts to prepare WLST scripts, transformed configs, and enterprise migration plan.";

        NotifyProceedState();
        return Task.CompletedTask;
    }

    public override void ApplyToMigrationConfiguration(MigrationConfiguration config)
    {
        if (_sessionConfig?.Transformation is not null)
        {
            config.Transformation              = _sessionConfig.Transformation;
            config.TransformationCompleted     = _sessionConfig.TransformationCompleted;
            config.TransformationWorkspacePath = _sessionConfig.TransformationWorkspacePath;
            config.FormsModernization          = _sessionConfig.FormsModernization;
            config.ReportsModernization        = _sessionConfig.ReportsModernization;
        }
    }

    private void NotifyProceedState()
    {
        OnPropertyChanged(nameof(HasTransformation));
        OnPropertyChanged(nameof(IsEmptyState));
        OnPropertyChanged(nameof(CanProceed));
    }
}
