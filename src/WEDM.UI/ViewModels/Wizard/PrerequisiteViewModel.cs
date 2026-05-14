using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WEDM.Application.Services;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Step 6: Prerequisite validation.
/// Runs all system checks (OS, RAM, disk, ports, JDK, VC++) and presents results
/// in a color-coded grid. The user cannot proceed until all Fatal/Error checks pass.
/// </summary>
public sealed partial class PrerequisiteViewModel : WizardStepViewModel
{
    private readonly DeploymentOrchestrator _orchestrator;
    private DeploymentConfiguration?        _config;

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private bool _hasRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private bool _allPassed;

    [ObservableProperty]
    private string _summaryMessage = "Click 'Run Checks' to validate your system.";

    [ObservableProperty]
    private string _passCount  = "–";

    [ObservableProperty]
    private string _warnCount  = "–";

    [ObservableProperty]
    private string _errorCount = "–";

    public ObservableCollection<FindingRowViewModel> Findings { get; } = new();

    public override bool CanProceed => HasRun && AllPassed;

    public PrerequisiteViewModel(DeploymentOrchestrator orchestrator)
    {
        _orchestrator   = orchestrator;
        StepTitle       = "Prerequisites";
        StepDescription = "Validate system requirements before deployment begins.";
        StepIcon        = "✅";
    }

    // ── Navigation hook — receive config snapshot from parent ──────────────────

    public override async Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        _config = config;
        // Auto-run checks when the user navigates to this step
        if (!HasRun)
            await RunChecksAsync();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RunChecksAsync()
    {
        if (_config is null) return;

        HasRun = false;
        Findings.Clear();
        SetBusy(true, "Running prerequisite checks...");
        ClearError();

        try
        {
            var result = await _orchestrator.ValidatePrerequisitesAsync(_config);

            foreach (var f in result.Findings)
                Findings.Add(new FindingRowViewModel(f));

            PassCount  = result.PassCount.ToString();
            WarnCount  = result.WarnCount.ToString();
            ErrorCount = result.ErrorCount.ToString();
            AllPassed  = result.CanProceed;

            SummaryMessage = result.CanProceed
                ? $"✔  All critical checks passed — {result.WarnCount} warning(s). Ready to deploy."
                : $"✘  {result.ErrorCount} error(s) and {result.Fatals} fatal issue(s) detected. Resolve before proceeding.";

            HasRun = true;
        }
        catch (Exception ex)
        {
            SetError($"Prerequisite check failed: {ex.Message}");
            SummaryMessage = "An error occurred while running checks.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        // No configuration changes — this step is read-only
    }
}

/// <summary>Row view-model for a single <see cref="ValidationFinding"/> in the check grid.</summary>
public sealed class FindingRowViewModel(ValidationFinding finding)
{
    public string CheckName    { get; } = finding.CheckName;
    public string Message      { get; } = finding.Message;
    public string Remediation  { get; } = finding.Remediation ?? string.Empty;
    public bool   Passed       { get; } = finding.Passed;

    public string StatusIcon => finding.Passed ? "✔" : finding.Severity switch
    {
        ValidationSeverity.Warning => "⚠",
        ValidationSeverity.Error   => "✘",
        ValidationSeverity.Fatal   => "💀",
        _                          => "ℹ"
    };

    public System.Windows.Media.Brush StatusColor => finding.Passed
        ? MakeBrush("#3FB950")
        : finding.Severity switch
        {
            ValidationSeverity.Warning => MakeBrush("#D29922"),
            ValidationSeverity.Error   => MakeBrush("#F85149"),
            ValidationSeverity.Fatal   => MakeBrush("#FF0000"),
            _                          => MakeBrush("#8B949E"),
        };

    private static System.Windows.Media.SolidColorBrush MakeBrush(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
}
