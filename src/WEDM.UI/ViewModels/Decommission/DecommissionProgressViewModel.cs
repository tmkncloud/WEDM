using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEDM.Application.Services;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Decommission;

public sealed partial class DecommissionProgressViewModel : DecommissionWizardStepViewModel
{
    private readonly DecommissionOrchestrator _orchestrator;
    private readonly ILoggingService _log;

    [ObservableProperty] private string _statusMessage = "Ready to decommission.";
    [ObservableProperty] private string _reportPath = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isComplete;

    public ObservableCollection<string> LogLines { get; } = [];

    public DecommissionProgressViewModel(DecommissionOrchestrator orchestrator, ILoggingService log)
    {
        _orchestrator = orchestrator;
        _log          = log;
        StepTitle       = "Decommission Progress";
        StepDescription = "Removing Oracle middleware environment.";
        StepIcon        = "🧹";
        _log.EntryWritten += OnLogEntry;
    }

    public override bool CanProceed => IsComplete;

    public override async Task OnNavigatingToAsync(DecommissionConfiguration config)
    {
        if (IsRunning || IsComplete) return;
        await RunDecommissionAsync(config);
    }

    public override void ApplyToDecommissionConfiguration(DecommissionConfiguration config) { }

    [RelayCommand]
    private void OpenReport()
    {
        if (string.IsNullOrWhiteSpace(ReportPath) || !File.Exists(ReportPath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = ReportPath,
            UseShellExecute = true,
        });
    }

    private async Task RunDecommissionAsync(DecommissionConfiguration config)
    {
        IsRunning = true;
        try
        {
            var report = await _orchestrator.ExecuteDecommissionAsync(config);
            StatusMessage = $"Decommission {report.FinalStatus} — {report.StepsSucceeded}/{report.TotalSteps} steps succeeded.";
            var html = Directory.GetFiles(config.Paths.ReportsDirectory, "wedm-decommission-*.html")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (html is not null) ReportPath = html;
            IsComplete = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Decommission failed: {ex.Message}";
            SetError(ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void OnLogEntry(object? sender, LogEntry entry)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            LogLines.Add($"[{entry.Timestamp:HH:mm:ss}] {entry.Message}"));
    }
}
