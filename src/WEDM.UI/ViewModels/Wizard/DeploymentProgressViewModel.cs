using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WEDM.Application.Services;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Step 7: Live deployment progress view.
/// Orchestrates the actual deployment execution and streams real-time output.
///
/// Features:
///   • Real-time step-level progress bars
///   • Live scrolling log viewer with color-coded severity
///   • Overall progress bar (0–100%)
///   • Pause / Cancel / Retry controls
///   • Elapsed time counter
///   • Per-step timing display
///
/// Thread safety: All UI updates are marshalled to the Dispatcher.
/// </summary>
public sealed partial class DeploymentProgressViewModel : WizardStepViewModel
{
    private readonly DeploymentOrchestrator _orchestrator;
    private readonly ILoggingService        _log;
    private CancellationTokenSource?        _cts;
    private DeploymentConfiguration?        _activeConfig;
    private System.Timers.Timer?            _elapsedTimer;
    private DateTimeOffset                  _startTime;

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverallProgressLabel))]
    private double _overallProgress;

    [ObservableProperty]
    private string _currentStepName = "Initializing...";

    [ObservableProperty]
    private string _currentStepDescription = string.Empty;

    [ObservableProperty]
    private string _elapsedTime = "00:00:00";

    [ObservableProperty]
    private bool _isDeploying;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private string _finalStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportCommand))]
    private string _reportPath = string.Empty;

    public bool CanOpenReport => !string.IsNullOrWhiteSpace(ReportPath) && File.Exists(ReportPath);

    public string OverallProgressLabel => $"{OverallProgress:F0}%";

    public ObservableCollection<LogEntryViewModel>  LogEntries   { get; } = new();
    public ObservableCollection<StepStatusViewModel> StepStatuses { get; } = new();

    public DeploymentProgressViewModel(DeploymentOrchestrator orchestrator, ILoggingService log)
    {
        _orchestrator = orchestrator;
        _log          = log;
        StepTitle       = "Deployment";
        StepDescription = "Installing and configuring Oracle WebLogic middleware.";
        StepIcon        = "🚀";

        // Subscribe to logging events
        _log.EntryWritten += OnLogEntryWritten;
        _orchestrator.StepProgressChanged  += OnStepProgressChanged;
        _orchestrator.OverallProgressChanged += OnOverallProgressChanged;
    }

    public override async Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        if (!IsDeploying && !IsCompleted && !IsFailed)
            await StartDeploymentAsync(config);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task StartDeploymentAsync(DeploymentConfiguration config)
    {
        if (IsDeploying) return;

        _activeConfig  = config;
        _cts           = new CancellationTokenSource();
        IsDeploying    = true;
        IsFailed       = false;
        IsCompleted    = false;
        CanRetry       = false;
        OverallProgress = 0;
        LogEntries.Clear();
        StepStatuses.Clear();

        _startTime = DateTimeOffset.UtcNow;
        StartElapsedTimer();
        SetBusy(true, "Deploying Oracle WebLogic...");

        try
        {
            var report = await _orchestrator.ExecuteDeploymentAsync(config, _cts.Token);

            IsCompleted  = report.FinalStatus == Domain.Enums.DeploymentStatus.Completed;
            IsFailed     = !IsCompleted;
            CanRetry     = IsFailed;
            FinalStatusMessage = IsCompleted
                ? $"✔  Deployment completed successfully in {report.TotalDuration?.ToString(@"hh\:mm\:ss")}"
                : BuildFailureMessage(report);

            ResolveReportPath(config, report);
        }
        catch (OperationCanceledException)
        {
            IsFailed           = true;
            CanRetry           = true;
            FinalStatusMessage = "Deployment was cancelled.";
            if (_activeConfig is not null)
                ResolveReportPath(_activeConfig, report: null);
        }
        catch (Exception ex)
        {
            IsFailed           = true;
            CanRetry           = true;
            FinalStatusMessage = $"Unexpected error: {ex.Message}";
            SetError(ex.Message);
            if (_activeConfig is not null)
                ResolveReportPath(_activeConfig, report: null);
        }
        finally
        {
            IsDeploying = false;
            StopElapsedTimer();
            SetBusy(false);
        }
    }

    [RelayCommand]
    private void CancelDeployment()
    {
        _cts?.Cancel();
        AddLogLine("⚠ Cancellation requested — waiting for current step to complete...",
            Domain.Enums.LogLevel.Warning);
    }

    [RelayCommand(CanExecute = nameof(CanOpenReport))]
    private void OpenReport()
    {
        var path = ReportPath;
        if (string.IsNullOrWhiteSpace(path) && _activeConfig is not null)
            ResolveReportPath(_activeConfig, report: null);

        path = ReportPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetError("Deployment report file was not found. Check the reports folder.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetError($"Could not open report: {ex.Message}");
        }
    }

    partial void OnReportPathChanged(string value)
        => OpenReportCommand.NotifyCanExecuteChanged();

    private void ResolveReportPath(DeploymentConfiguration config, DeploymentReport? report)
    {
        var reportsDir = config.Paths.ReportsDirectory;
        if (string.IsNullOrWhiteSpace(reportsDir))
            return;

        Directory.CreateDirectory(reportsDir);

        if (report is not null)
        {
            var expected = Path.Combine(reportsDir, $"wedm-report-{report.ReportId:N}.html");
            if (File.Exists(expected))
            {
                ReportPath = expected;
                return;
            }
        }

        var deploymentReport = Directory.Exists(reportsDir)
            ? Directory.GetFiles(reportsDir, "wedm-report-*.html")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName
            : null;

        if (!string.IsNullOrWhiteSpace(deploymentReport))
        {
            ReportPath = deploymentReport;
            return;
        }

        var workflowReport = Directory.Exists(reportsDir)
            ? Directory.GetFiles(reportsDir, "wedm-workflow-*.html")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName
            : null;

        if (!string.IsNullOrWhiteSpace(workflowReport))
            ReportPath = workflowReport;
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config) { }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnLogEntryWritten(object? sender, LogEntry entry)
    {
        DispatchUI(() => LogEntries.Add(new LogEntryViewModel(entry)));
    }

    private void OnStepProgressChanged(object? sender, StepProgressEventArgs e)
    {
        DispatchUI(() =>
        {
            CurrentStepName        = e.Step.Name;
            CurrentStepDescription = e.Step.Description;

            var existing = StepStatuses.FirstOrDefault(s => s.StepName == e.Step.Name);
            if (existing is null)
                StepStatuses.Add(new StepStatusViewModel(e.Step));
            else
                existing.Update(e.Step);
        });
    }

    private void OnOverallProgressChanged(object? sender, double pct)
        => DispatchUI(() => OverallProgress = pct);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void AddLogLine(string message, Domain.Enums.LogLevel level)
    {
        DispatchUI(() => LogEntries.Add(new LogEntryViewModel(
            new LogEntry { Message = message, Level = level })));
    }

    private void StartElapsedTimer()
    {
        _elapsedTimer = new System.Timers.Timer(1000);
        _elapsedTimer.Elapsed += (_, _) =>
        {
            var elapsed = DateTimeOffset.UtcNow - _startTime;
            DispatchUI(() => ElapsedTime = elapsed.ToString(@"hh\:mm\:ss"));
        };
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer?.Dispose();
        _elapsedTimer = null;
    }

    private static string BuildFailureMessage(DeploymentReport report)
    {
        var msg = $"✘  Deployment failed — {report.StepsFailed} step(s) failed.";
        if (report.Validation is null)
            return msg + " Check logs below.";

        var blockers = report.Validation.Findings
            .Where(f => !f.Passed && f.Severity >= Domain.Enums.ValidationSeverity.Error)
            .Select(f => f.CheckName)
            .Take(4)
            .ToList();

        return blockers.Count == 0
            ? msg + " Check logs below."
            : $"{msg} Failed checks: {string.Join(", ", blockers)}. See log for remediation.";
    }
}

// ── UI Model helpers ─────────────────────────────────────────────────────────

public sealed partial class LogEntryViewModel(LogEntry entry) : ObservableObject
{
    public DateTimeOffset Timestamp { get; } = entry.Timestamp;
    public string         Message   { get; } = entry.Message;
    public string         Category  { get; } = entry.Category;

    public string LevelIcon => entry.Level switch
    {
        Domain.Enums.LogLevel.Error   => "✘",
        Domain.Enums.LogLevel.Fatal   => "💀",
        Domain.Enums.LogLevel.Warning => "⚠",
        Domain.Enums.LogLevel.Info    => "ℹ",
        Domain.Enums.LogLevel.Verbose => "·",
        _                              => "·"
    };

    public System.Windows.Media.Brush LevelColor => entry.Level switch
    {
        Domain.Enums.LogLevel.Error   => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F85149")),
        Domain.Enums.LogLevel.Fatal   => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF0000")),
        Domain.Enums.LogLevel.Warning => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D29922")),
        Domain.Enums.LogLevel.Info    => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8B949E")),
        _                              => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6E7681")),
    };
}

public sealed partial class StepStatusViewModel(DeploymentStep step) : ObservableObject
{
    public string StepName { get; } = step.Name;

    [ObservableProperty] private Domain.Enums.StepStatus _status = step.Status;
    [ObservableProperty] private double _progress = step.ProgressPercent;
    [ObservableProperty] private string _duration = step.Duration?.ToString(@"mm\:ss") ?? "--:--";

    public void Update(DeploymentStep updated)
    {
        Status   = updated.Status;
        Progress = updated.ProgressPercent;
        Duration = updated.Duration?.ToString(@"mm\:ss") ?? "--:--";
    }

    public string StatusIcon => Status switch
    {
        Domain.Enums.StepStatus.Succeeded => "✔",
        Domain.Enums.StepStatus.Failed    => "✘",
        Domain.Enums.StepStatus.Running   => "⟳",
        Domain.Enums.StepStatus.Skipped   => "⊘",
        _                                  => "○"
    };
}
