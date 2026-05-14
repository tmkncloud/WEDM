using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using WEDM.Application.Services;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Opatch;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Patch Management: staging path, OPatch validation, inventory view, and patch-only workflow execution.
/// </summary>
public sealed partial class PatchManagementViewModel : WizardStepViewModel
{
    private readonly DeploymentOrchestrator _orch;
    private readonly OpatchRunner          _opatch;
    private readonly IPatchExecutionState  _patchState;

    private DeploymentConfiguration? _sessionConfig;

    [ObservableProperty] private string _patchStagingDirectory = string.Empty;

    [ObservableProperty] private bool _includePatchesInDeployment;

    [ObservableProperty] private bool _useNapply = true;

    [ObservableProperty] private bool _runConflictPrerequisites = true;

    [ObservableProperty] private bool _captureInventorySnapshots = true;

    [ObservableProperty] private bool _checkForRunningMiddlewareProcesses = true;

    [ObservableProperty] private string _opatchBatPathOverride = string.Empty;

    [ObservableProperty] private int _opatchTimeoutMinutes = 180;

    [ObservableProperty] private string _readinessSummary = string.Empty;

    [ObservableProperty] private string _opatchVersionText = string.Empty;

    [ObservableProperty] private string _inventoryRawText = string.Empty;

    [ObservableProperty] private string _lastRunStatus = string.Empty;

    [ObservableProperty] private string _patchReportHtmlPath = string.Empty;

    public ObservableCollection<AppliedPatchRecord> ParsedInventoryPatches { get; } = new();

    public override bool CanProceed => true;

    public PatchManagementViewModel(DeploymentOrchestrator orch, OpatchRunner opatch, IPatchExecutionState patchState)
    {
        _orch        = orch;
        _opatch      = opatch;
        _patchState  = patchState;
        StepTitle       = "Patch Management";
        StepDescription = "OPatch validation, staging, inventory, and patch-only runs.";
        StepIcon        = "🩹";
    }

    public override Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        _sessionConfig = config;
        PatchStagingDirectory               = config.Patches.PatchStagingDirectory;
        IncludePatchesInDeployment          = config.Patches.Enabled && !config.Patches.StandalonePatchWorkflow;
        UseNapply                           = config.Patches.UseNapply;
        RunConflictPrerequisites            = config.Patches.RunConflictPrerequisites;
        CaptureInventorySnapshots           = config.Patches.CaptureInventorySnapshots;
        CheckForRunningMiddlewareProcesses  = config.Patches.CheckForRunningMiddlewareProcesses;
        OpatchBatPathOverride               = config.Patches.OpatchBatPathOverride;
        OpatchTimeoutMinutes                = config.Patches.OpatchTimeoutMinutes;
        ReadinessSummary                    = string.Empty;
        OpatchVersionText                   = string.Empty;
        LastRunStatus                       = string.Empty;
        PatchReportHtmlPath                 = string.Empty;
        InventoryRawText                    = string.Empty;
        ParsedInventoryPatches.Clear();
        return Task.CompletedTask;
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        config.Patches.PatchStagingDirectory               = PatchStagingDirectory;
        config.Patches.Enabled                             = IncludePatchesInDeployment;
        config.Patches.StandalonePatchWorkflow               = false;
        config.Patches.UseNapply                             = UseNapply;
        config.Patches.RunConflictPrerequisites            = RunConflictPrerequisites;
        config.Patches.CaptureInventorySnapshots             = CaptureInventorySnapshots;
        config.Patches.CheckForRunningMiddlewareProcesses  = CheckForRunningMiddlewareProcesses;
        config.Patches.OpatchBatPathOverride                 = OpatchBatPathOverride;
        config.Patches.OpatchTimeoutMinutes                  = Math.Clamp(OpatchTimeoutMinutes, 15, 1440);
    }

    [RelayCommand]
    private void BrowsePatchStaging()
    {
        var dlg = new OpenFolderDialog { Title = "Select patch staging directory (CPU/PSU root or single patch folder)" };
        if (dlg.ShowDialog() == true)
            PatchStagingDirectory = dlg.FolderName;
    }

    [RelayCommand]
    private async Task ValidateReadinessAsync()
    {
        if (_sessionConfig is null) return;
        ApplyToConfiguration(_sessionConfig);
        var savedEnabled = _sessionConfig.Patches.Enabled;
        _sessionConfig.Patches.Enabled = true;
        SetBusy(true, "Validating patch readiness...");
        try
        {
            var r = await _orch.ValidatePatchReadinessAsync(_sessionConfig);
            ReadinessSummary = r.Validation.CanProceed
                ? $"Checks passed (errors={r.Validation.ErrorCount}, warnings={r.Validation.WarnCount})."
                : $"Validation failed — {r.Validation.Fatals} fatal, {r.Validation.FailedCount} errors.";
            OpatchVersionText = r.OpatchVersionExitCode == 0
                ? r.OpatchVersionOutput ?? string.Empty
                : $"OPatch version failed (exit {r.OpatchVersionExitCode}):\n{r.OpatchVersionOutput}";
        }
        finally
        {
            _sessionConfig.Patches.Enabled = savedEnabled;
            SetBusy(false);
        }
    }

    [RelayCommand]
    private async Task RefreshInventoryAsync()
    {
        if (_sessionConfig is null) return;
        ApplyToConfiguration(_sessionConfig);
        var savedEnabled = _sessionConfig.Patches.Enabled;
        _sessionConfig.Patches.Enabled = true;
        SetBusy(true, "Running opatch lsinventory...");
        try
        {
            var inv = await _opatch.LsinventoryAsync(_sessionConfig, default);
            InventoryRawText = inv.Output + (string.IsNullOrEmpty(inv.Errors) ? string.Empty : "\n" + inv.Errors);
            ParsedInventoryPatches.Clear();
            foreach (var p in OpatchInventoryParser.Parse(inv.Output))
                ParsedInventoryPatches.Add(p);
        }
        finally
        {
            _sessionConfig.Patches.Enabled = savedEnabled;
            SetBusy(false);
        }
    }

    [RelayCommand]
    private async Task RunPatchOnlyAsync()
    {
        if (_sessionConfig is null) return;
        ApplyToConfiguration(_sessionConfig);
        SetBusy(true, "Executing OPatch workflow — this may take a long time...");
        LastRunStatus       = string.Empty;
        PatchReportHtmlPath = string.Empty;
        try
        {
            var report = await _orch.ExecutePatchWorkflowAsync(_sessionConfig, default);
            LastRunStatus = report.FinalStatus == DeploymentStatus.Completed
                ? $"Patch workflow completed: {report.StepsSucceeded}/{report.TotalSteps} steps succeeded."
                : $"Patch workflow finished with status {report.FinalStatus} ({report.StepsFailed} failed).";

            var dir = _sessionConfig.Paths.ReportsDirectory;
            if (Directory.Exists(dir))
            {
                var latest = Directory.EnumerateFiles(dir, "wedm-opatch-*.html")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (latest is not null)
                    PatchReportHtmlPath = latest.FullName;
            }

            ParsedInventoryPatches.Clear();
            foreach (var p in _patchState.ParsedPostPatches)
                ParsedInventoryPatches.Add(p);
            if (ParsedInventoryPatches.Count == 0)
            {
                foreach (var p in _patchState.ParsedPrePatches)
                    ParsedInventoryPatches.Add(p);
            }
        }
        catch (OperationCanceledException)
        {
            LastRunStatus = "Patch workflow was cancelled.";
        }
        catch (Exception ex)
        {
            LastRunStatus = $"Patch workflow error: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private void OpenPatchReport()
    {
        if (string.IsNullOrWhiteSpace(PatchReportHtmlPath) || !File.Exists(PatchReportHtmlPath))
            return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = PatchReportHtmlPath,
            UseShellExecute = true
        });
    }
}
