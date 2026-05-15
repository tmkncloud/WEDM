using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Migration;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Migration;

public sealed partial class MigrationExecutionViewModel : MigrationWizardStepViewModel
{
    private readonly IMigrationExecutionOrchestrator _orchestrator;
    private MigrationConfiguration? _sessionConfig;
    private CancellationTokenSource? _executionCts;

    [ObservableProperty] private string _executionStatus = "Controlled migration execution requires operator approval at each critical checkpoint.";
    [ObservableProperty] private string _upgradePath = string.Empty;
    [ObservableProperty] private string _workspacePath = string.Empty;
    [ObservableProperty] private string _confidenceDisplay = string.Empty;
    [ObservableProperty] private bool _dryRun = true;
    [ObservableProperty] private bool _isExecuting;
    [ObservableProperty] private double _executionProgress;
    [ObservableProperty] private string _webLogicUsername = "weblogic";
    [ObservableProperty] private string _webLogicPassword = string.Empty;
    [ObservableProperty] private string _targetDomainHome = string.Empty;
    [ObservableProperty] private string _outcomeDisplay = "Not started";
    [ObservableProperty] private bool _hasPendingCheckpoint;
    [ObservableProperty] private string _checkpointTitle = string.Empty;
    [ObservableProperty] private string _checkpointDetail = string.Empty;
    [ObservableProperty] private string _operatorNote = string.Empty;

    public ObservableCollection<MigrationExecutionStageResult> ExecutionStages { get; } = [];
    public ObservableCollection<WlstExecutionRecord> WlstResults { get; } = [];
    public ObservableCollection<string> ExecutionLog { get; } = [];
    public ObservableCollection<GeneratedTransformationArtifact> WlstArtifacts { get; } = [];
    public ObservableCollection<PreflightCheckResult> PreflightChecks { get; } = [];

    public override bool CanProceed => !IsExecuting;

    public MigrationExecutionViewModel(IMigrationExecutionOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        _orchestrator.ProgressChanged += OnProgress;
        _orchestrator.CheckpointRequired += OnCheckpointRequired;

        StepTitle       = "Controlled Migration Execution";
        StepDescription = "Execute migration blueprint with operator checkpoints, dry-run support, and full audit trail.";
        StepIcon        = "🚀";
    }

    private void OnProgress(object? sender, MigrationExecutionProgressEventArgs e)
    {
        ExecutionProgress = e.OverallPercent;
        if (!string.IsNullOrWhiteSpace(e.LogLine))
            AppendLog(e.LogLine);

        var existing = ExecutionStages.FirstOrDefault(s => s.Stage == e.Stage.Stage);
        if (existing is not null)
            ExecutionStages[ExecutionStages.IndexOf(existing)] = e.Stage;
        else
            ExecutionStages.Add(e.Stage);
    }

    private void OnCheckpointRequired(object? sender, MigrationExecutionCheckpointEventArgs e)
    {
        HasPendingCheckpoint = true;
        CheckpointTitle      = e.Checkpoint.Title;
        CheckpointDetail     = e.Checkpoint.Detail;
        ExecutionStatus      = "Operator checkpoint — review and approve, pause, or abort.";
    }

    [RelayCommand]
    private async Task StartExecutionAsync()
    {
        if (_sessionConfig is null || !_sessionConfig.TransformationCompleted) return;

        _executionCts = new CancellationTokenSource();
        try
        {
            IsExecuting = true;
            IsBusy      = true;
            ClearError();
            ExecutionStages.Clear();
            WlstResults.Clear();
            ExecutionLog.Clear();
            PreflightChecks.Clear();
            HasPendingCheckpoint = false;
            SetBusy(true, DryRun ? "Running execution dry-run…" : "Running controlled migration execution…");

            var options = new MigrationExecutionOptions
            {
                DryRun              = DryRun,
                TargetDomainHome    = string.IsNullOrWhiteSpace(TargetDomainHome) ? null : TargetDomainHome,
                Credentials         = string.IsNullOrWhiteSpace(WebLogicPassword)
                    ? null
                    : new MigrationExecutionCredentials { WebLogicUsername = WebLogicUsername, WebLogicPassword = WebLogicPassword },
                OperationTimeoutMinutes = 90,
            };

            var result = await _orchestrator.ExecuteAsync(_sessionConfig, options, _executionCts.Token);

            _sessionConfig.Execution           = result;
            _sessionConfig.ExecutionCompleted  = result.Outcome is MigrationExecutionOutcome.Completed
                or MigrationExecutionOutcome.CompletedWithWarnings;
            _sessionConfig.ExecutionDurationMs = result.TotalDurationMs;

            OutcomeDisplay = result.Outcome.ToString();
            foreach (var w in result.WlstExecutions)
                WlstResults.Add(w);
            foreach (var c in result.Preflight.Checks)
                PreflightChecks.Add(c);

            ExecutionStatus = result.Outcome switch
            {
                MigrationExecutionOutcome.Completed => $"Execution completed successfully ({result.TotalDurationMs} ms).",
                MigrationExecutionOutcome.CompletedWithWarnings => $"Execution completed with warnings ({result.TotalDurationMs} ms).",
                MigrationExecutionOutcome.Cancelled => "Execution cancelled by operator.",
                MigrationExecutionOutcome.Paused => "Execution paused — resume from checkpoint when ready.",
                MigrationExecutionOutcome.Failed => "Execution failed — review logs and remediation guidance.",
                _ => ExecutionStatus,
            };

            ExecutionDiagnostics.Trace("Outcome", $"{result.Outcome} dryRun={DryRun} duration={result.TotalDurationMs}ms");
        }
        catch (Exception ex)
        {
            HandleException(ex, "Execution");
            ExecutionStatus = "Execution error — see diagnostics and workspace logs.";
        }
        finally
        {
            IsExecuting = false;
            IsBusy      = false;
            SetBusy(false);
            ExecutionProgress = 100;
            _executionCts?.Dispose();
            _executionCts = null;
            WebLogicPassword = string.Empty;
            OnPropertyChanged(nameof(CanProceed));
        }
    }

    [RelayCommand]
    private void ApproveCheckpoint()
    {
        SubmitCheckpoint(CheckpointDecisionKind.Approve);
    }

    [RelayCommand]
    private void PauseCheckpoint()
    {
        SubmitCheckpoint(CheckpointDecisionKind.Pause);
    }

    [RelayCommand]
    private void AbortCheckpoint()
    {
        SubmitCheckpoint(CheckpointDecisionKind.Abort);
    }

    [RelayCommand]
    private void CancelExecution()
    {
        _orchestrator.CancelActiveExecution();
        _executionCts?.Cancel();
        AppendLog("Cancel requested by operator.");
    }

    [RelayCommand]
    private void OpenWorkspace()
    {
        if (string.IsNullOrWhiteSpace(WorkspacePath) || !Directory.Exists(WorkspacePath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = WorkspacePath,
            UseShellExecute = true,
        });
    }

    private void SubmitCheckpoint(CheckpointDecisionKind kind)
    {
        _orchestrator.SubmitCheckpointDecision(new CheckpointDecision
        {
            Kind         = kind,
            OperatorNote = string.IsNullOrWhiteSpace(OperatorNote) ? null : OperatorNote,
        });
        ExecutionDiagnostics.TraceCheckpoint(CheckpointTitle, kind.ToString(), OperatorNote);
        HasPendingCheckpoint = false;
        OperatorNote         = string.Empty;
    }

    private void AppendLog(string line)
    {
        ExecutionLog.Add(line);
        if (ExecutionLog.Count > 500)
            ExecutionLog.RemoveAt(0);
    }

    public override Task OnNavigatingToAsync(MigrationConfiguration config)
    {
        _sessionConfig = config;
        UpgradePath    = MigrationVersionMatrix.DescribeUpgradePath(config.Source.Release, config.Target.Release);
        WorkspacePath  = config.TransformationWorkspacePath ?? string.Empty;
        ConfidenceDisplay = config.Transformation?.Confidence.ToString() ?? "Not assessed";
        TargetDomainHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WEDM", "target-domains", config.Topology.DomainName ?? "migration_domain");

        WlstArtifacts.Clear();
        if (config.Transformation?.Artifacts is not null)
        {
            foreach (var a in config.Transformation.Artifacts.Where(x => x.Kind == TransformationArtifactKind.WlstScript))
                WlstArtifacts.Add(a);
        }

        if (config.Execution is not null)
        {
            OutcomeDisplay = config.Execution.Outcome.ToString();
            foreach (var s in config.Execution.Stages)
                ExecutionStages.Add(s);
            foreach (var w in config.Execution.WlstExecutions)
                WlstResults.Add(w);
            foreach (var line in config.Execution.ExecutionLog.TakeLast(100))
                ExecutionLog.Add(line);
        }

        ExecutionStatus = config.TransformationCompleted
            ? (config.ExecutionCompleted
                ? $"Last execution: {config.Execution?.Outcome} — re-run or review workspace reports."
                : "Configure credentials, enable dry-run for rehearsal, then start controlled execution.")
            : "Complete migration preparation before execution.";

        return Task.CompletedTask;
    }

    public override void ApplyToMigrationConfiguration(MigrationConfiguration config)
    {
        if (_sessionConfig?.Execution is not null)
        {
            config.Execution          = _sessionConfig.Execution;
            config.ExecutionCompleted = _sessionConfig.ExecutionCompleted;
            config.ExecutionDurationMs = _sessionConfig.ExecutionDurationMs;
        }
    }
}
