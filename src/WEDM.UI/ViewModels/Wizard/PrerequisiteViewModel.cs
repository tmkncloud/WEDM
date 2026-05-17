using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WEDM.Application.Services;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
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
    private readonly DeploymentOrchestrator      _orchestrator;
    private readonly IOracleRemediationService?      _remediation;
    private readonly IOracleInventoryBootstrapService? _inventoryBootstrap;
    private DeploymentConfiguration?                  _config;

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

    [ObservableProperty]
    private bool _forceCleanInstall;

    [ObservableProperty]
    private bool _enableAutomaticInventoryBootstrap = true;

    [ObservableProperty]
    private string _bootstrapPreview = "Run checks to assess inventory bootstrap options.";

    [ObservableProperty]
    private bool _enableAutoRemediation = true;

    [ObservableProperty]
    private bool _safeCleanupOnly = true;

    [ObservableProperty]
    private string _remediationPreview = "Run checks to assess Oracle remediation options.";

    public ObservableCollection<FindingRowViewModel> Findings { get; } = new();

    public override bool CanProceed => HasRun && AllPassed;

    public PrerequisiteViewModel(
        DeploymentOrchestrator orchestrator,
        IOracleRemediationService? remediation = null,
        IOracleInventoryBootstrapService? inventoryBootstrap = null)
    {
        _orchestrator = orchestrator;
        _remediation  = remediation;
        _inventoryBootstrap = inventoryBootstrap;
        StepTitle       = "Prerequisites";
        StepDescription = "Validate system requirements before deployment begins.";
        StepIcon        = "✅";
    }

    // ── Navigation hook — receive config snapshot from parent ──────────────────

    public override async Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        _config = config;
        ForceCleanInstall      = config.OracleLifecycle.ForceCleanInstall;
        EnableAutomaticInventoryBootstrap = config.OracleLifecycle.EnableAutomaticInventoryBootstrap;
        EnableAutoRemediation  = config.OracleLifecycle.EnableAutoRemediation;
        SafeCleanupOnly        = config.OracleLifecycle.SafeCleanupOnly;
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
            RefreshRemediationPreview();
            RefreshBootstrapPreview();
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

    [RelayCommand]
    private async Task PreviewRemediationAsync()
    {
        if (_config is null || _remediation is null) return;

        ApplyToConfiguration(_config);
        SetBusy(true, "Running remediation dry-run preview...");
        try
        {
            var assessment = _remediation.Assess(_config, "PrerequisitePreview");
            var result = await _remediation.ExecuteAsync(
                _config,
                new RemediationExecutionOptions { DryRun = true, Trigger = "PrerequisitePreview" });

            var plan = assessment.RecommendedPlan;
            RemediationPreview =
                $"Classification: {assessment.Classification} | Safe: {assessment.Safety.IsSafeToRemediate} | " +
                $"Actions: {plan?.Actions.Count ?? 0} | Risk: {assessment.Safety.Risk} | " +
                $"Confidence: {assessment.Safety.Confidence}\n" +
                string.Join("\n", result.Report.ExecutedActions.Select(a => $"  • {a.ActionType}: {a.TargetPath}"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshRemediationPreview()
    {
        if (_config is null || _remediation is null)
        {
            RemediationPreview = "Remediation engine not available.";
            return;
        }

        var assessment = _remediation.Assess(_config, "ValidatePrerequisites");
        if (!assessment.RequiresRemediation)
        {
            RemediationPreview = "No Oracle partial-install remediation required.";
            return;
        }

        var plan = assessment.RecommendedPlan;
        RemediationPreview =
            $"{assessment.Classification}: {assessment.Issues.FirstOrDefault()?.Message}\n" +
            $"Planned actions: {plan?.Actions.Count ?? 0} | Safe auto-repair: {assessment.CanAutoRemediate} | " +
            $"{assessment.Safety.Recommendation}";
    }

    [RelayCommand]
    private async Task PreviewBootstrapAsync()
    {
        if (_config is null || _inventoryBootstrap is null) return;

        ApplyToConfiguration(_config);
        SetBusy(true, "Running inventory bootstrap dry-run...");
        try
        {
            var result = await _inventoryBootstrap.EnsureInventoryReadyAsync(
                _config,
                new InventoryBootstrapExecutionOptions { DryRun = true, Trigger = "PrerequisitePreview" });

            BootstrapPreview =
                $"Success: {result.Success} | Root: {result.Report.InventoryRoot}\n" +
                $"Version: {result.Report.VersionProfile}\n" +
                string.Join("\n", result.Report.WrittenFiles.Select(f => $"  • {f}"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshBootstrapPreview()
    {
        if (_config is null || _inventoryBootstrap is null)
        {
            BootstrapPreview = "Inventory bootstrap engine not available.";
            return;
        }

        var assessment = _inventoryBootstrap.Assess(_config);
        if (!assessment.RequiresBootstrap)
        {
            BootstrapPreview = "Central Oracle inventory is present — no bootstrap required.";
            return;
        }

        BootstrapPreview =
            $"State: {assessment.State} | Safe: {assessment.Safety.IsSafe} | " +
            $"Auto-bootstrap: {assessment.CanAutoBootstrap}\n" +
            $"{assessment.Plan?.Summary}";
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        config.OracleLifecycle.ForceCleanInstall     = ForceCleanInstall;
        config.OracleLifecycle.EnableAutomaticInventoryBootstrap = EnableAutomaticInventoryBootstrap;
        config.OracleLifecycle.EnableAutoRemediation = EnableAutoRemediation;
        config.OracleLifecycle.SafeCleanupOnly       = SafeCleanupOnly;
        if (EnableAutoRemediation && config.OracleLifecycle.AutoRemediationMode == AutoRemediationMode.Disabled)
            config.OracleLifecycle.AutoRemediationMode = AutoRemediationMode.AutomaticSafeOnly;
    }
}

/// <summary>
/// Row view-model for a single <see cref="ValidationFinding"/> in the check grid.
/// Exposes all diagnostic fields required for operator-grade failure analysis:
///   • CheckName, Message, Remediation
///   • ActualValue / ExpectedValue — displayed when non-null
///   • Severity label and color-coded icon
///   • Category derived from dot-separated CheckName prefix
/// </summary>
public sealed class FindingRowViewModel(ValidationFinding finding)
{
    public string  CheckName      { get; } = finding.CheckName;
    public string  Message        { get; } = finding.Message;
    public string  Remediation    { get; } = finding.Remediation ?? string.Empty;
    public bool    Passed         { get; } = finding.Passed;

    // ── Diagnostic detail fields ──────────────────────────────────────────────
    public string  ActualValue    { get; } = finding.ActualValue?.ToString()   ?? string.Empty;
    public string  ExpectedValue  { get; } = finding.ExpectedValue?.ToString() ?? string.Empty;
    public string  SeverityLabel  { get; } = finding.Passed ? "PASS" : finding.Severity.ToString().ToUpper();
    public string  Category       { get; } = DeriveCategory(finding.CheckName);

    /// <summary>True when the ActualValue / ExpectedValue comparison row should be shown.</summary>
    public bool HasDiagnosticValues => !finding.Passed
        && (!string.IsNullOrWhiteSpace(finding.ActualValue?.ToString())
         || !string.IsNullOrWhiteSpace(finding.ExpectedValue?.ToString()));

    /// <summary>Human-readable comparison line, e.g. "Expected: JDK 8   Actual: Not installed"</summary>
    public string DiagnosticSummary
    {
        get
        {
            if (!HasDiagnosticValues) return string.Empty;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ExpectedValue)) parts.Add($"Expected: {ExpectedValue}");
            if (!string.IsNullOrWhiteSpace(ActualValue))   parts.Add($"Actual: {ActualValue}");
            return string.Join("   |   ", parts);
        }
    }

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

    public System.Windows.Media.Brush CategoryColor => Category switch
    {
        "JDK"      or "Java"   => MakeBrush("#2188FF"),
        "Payload"  or "Middleware" => MakeBrush("#6F42C1"),
        "Port"     or "Ports"  => MakeBrush("#E36209"),
        "Database" or "DB"     => MakeBrush("#0366D6"),
        "OS"       or "OSVersion" or "OSArchitecture" => MakeBrush("#1B7F79"),
        "Disk"     or "DiskSpace" => MakeBrush("#8B6914"),
        "RAM"      or "CPU"    => MakeBrush("#B31D28"),
        _                      => MakeBrush("#586069"),
    };

    private static string DeriveCategory(string checkName)
    {
        if (string.IsNullOrWhiteSpace(checkName)) return "General";
        var dot = checkName.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? checkName[..dot] : checkName;
    }

    private static System.Windows.Media.SolidColorBrush MakeBrush(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
}
