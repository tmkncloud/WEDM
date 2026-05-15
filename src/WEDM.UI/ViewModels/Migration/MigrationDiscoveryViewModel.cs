using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Migration;

public sealed partial class MigrationDiscoveryViewModel : MigrationWizardStepViewModel
{
    private readonly IMiddlewareDiscoveryOrchestrator _orchestrator;
    private MigrationConfiguration? _sessionConfig;

    [ObservableProperty]
    private bool _scanInProgress;

    [ObservableProperty]
    private double _discoveryProgress;

    [ObservableProperty]
    private string _scanStatusMessage = "Run environment discovery to analyze real middleware topology and Forms / Reports inventory.";

    [ObservableProperty]
    private string _statusBannerKind = "Info";

    [ObservableProperty]
    private string _scanModeDisplay = "Read-only environment scan";

    [ObservableProperty]
    private MiddlewareTopologySnapshot _topology = new();

    [ObservableProperty]
    private FormsReportsMetadataSnapshot _formsMetadata = new();

    [ObservableProperty]
    private OracleInventorySnapshot _oracleInventory = new();

    [ObservableProperty]
    private string _middlewareHome = @"D:\Oracle\Middleware";

    [ObservableProperty]
    private string _domainHome = @"D:\Oracle\user_projects\domains\base_domain";

    public ObservableCollection<ManagedServerDescriptor> ManagedServers { get; } = [];
    public ObservableCollection<EnvironmentDiscoveryFinding> DiscoveryInsights { get; } = [];
    public ObservableCollection<string> JvmArguments { get; } = [];
    public ObservableCollection<DiscoveryStageResult> DiscoveryStages { get; } = [];
    public ObservableCollection<PatchInventoryRecord> PatchInventory { get; } = [];
    public ObservableCollection<string> DiscoveryWarnings { get; } = [];

    public bool HasDiscoveryResults =>
        Topology.ScanStatus is DiscoveryScanStatus.Completed or DiscoveryScanStatus.Partial;

    public bool IsEmptyState => !HasDiscoveryResults && !ScanInProgress;

    public bool HasDiscoveryWarnings => DiscoveryWarnings.Count > 0;

    public override bool CanProceed => HasDiscoveryResults && !ScanInProgress;

    public MigrationDiscoveryViewModel(IMiddlewareDiscoveryOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        _orchestrator.ProgressChanged += OnDiscoveryProgress;

        StepTitle       = "Source Environment Discovery";
        StepDescription = "Read-only scanning of middleware homes, WebLogic domains, Forms/Reports, and patch inventory.";
        StepIcon        = "🔍";
    }

    private void OnDiscoveryProgress(object? sender, DiscoveryProgressEventArgs e)
    {
        DiscoveryProgress = e.OverallPercent;
        ScanStatusMessage = $"{e.Stage.DisplayName}: {e.Stage.Status}";

        var existing = DiscoveryStages.FirstOrDefault(s => s.Stage == e.Stage.Stage);
        if (existing is not null)
        {
            var idx = DiscoveryStages.IndexOf(existing);
            DiscoveryStages[idx] = e.Stage;
        }
        else
        {
            DiscoveryStages.Add(e.Stage);
        }
    }

    [RelayCommand]
    private async Task RunDiscoveryAsync()
    {
        if (_sessionConfig is null) return;

        var sw = Stopwatch.StartNew();
        try
        {
            ScanInProgress = true;
            IsBusy         = true;
            ClearError();
            DiscoveryStages.Clear();
            DiscoveryWarnings.Clear();
            SetBusy(true, "Running environment discovery pipeline…");
            StatusBannerKind  = "Progress";
            ScanModeDisplay     = "Read-only scan in progress";

            _sessionConfig.Source.MiddlewareHome = MiddlewareHome;
            _sessionConfig.Source.DomainHome     = DomainHome;

            var options = new DiscoveryScanOptions
            {
                MiddlewareHome         = MiddlewareHome,
                DomainHome             = DomainHome,
                AllowSimulatedFallback = true,
                ScanTimeoutSeconds     = 120,
            };

            var result = await _orchestrator.ExecuteAsync(_sessionConfig.Source, options);

            Topology         = result.Topology;
            FormsMetadata    = result.FormsMetadata;
            OracleInventory  = result.OracleInventory;
            RefreshCollections(result);

            _sessionConfig.Topology              = result.Topology;
            _sessionConfig.FormsMetadata         = result.FormsMetadata;
            _sessionConfig.OracleInventory       = result.OracleInventory;
            _sessionConfig.DomainAnalysis        = result.DomainAnalysis;
            _sessionConfig.DiscoveryInsights     = result.Insights.ToList();
            _sessionConfig.DiscoveryStages         = result.Stages.ToList();
            _sessionConfig.DiscoveryWarnings       = result.Warnings.ToList();
            _sessionConfig.DiscoveryCompleted      = HasDiscoveryResults;
            _sessionConfig.DiscoveryUsedRealScan   = result.UsedRealScan;
            _sessionConfig.DiscoveryDurationMs     = result.TotalDurationMs;

            ScanModeDisplay = result.UsedRealScan
                ? "Real environment scan"
                : "Assessment preview (paths inaccessible — limited analysis)";

            StatusBannerKind = result.ScanStatus switch
            {
                DiscoveryScanStatus.Completed => "Success",
                DiscoveryScanStatus.Partial   => "Warning",
                _                             => "Error",
            };

            ScanStatusMessage =
                $"Discovery finished in {result.TotalDurationMs} ms — {FormsMetadata.FormCount} forms, " +
                $"{Topology.ManagedServerCount} managed servers, {OracleInventory.Patches.Count} patches.";

            MigrationDiagnostics.TraceTiming("Discovery", sw.ElapsedMilliseconds, ScanModeDisplay);
            MigrationDiagnostics.Trace("DiscoveryMode", result.UsedRealScan ? "RealScan" : "Fallback");
            foreach (var stage in result.Stages)
                MigrationDiagnostics.TraceDiscoveryStage(stage.DisplayName, stage.Status.ToString(), stage.DurationMs);
            MigrationDiagnostics.TraceDiscoveryWarnings(result.Warnings);
        }
        catch (Exception ex)
        {
            StatusBannerKind  = "Error";
            ScanStatusMessage = "Discovery could not complete. Verify paths, permissions, and retry.";
            HandleException(ex, "Discovery");
        }
        finally
        {
            ScanInProgress = false;
            IsBusy         = false;
            SetBusy(false);
            DiscoveryProgress = HasDiscoveryResults ? 100 : 0;
            OnPropertyChanged(nameof(CanProceed));
            OnPropertyChanged(nameof(HasDiscoveryResults));
            OnPropertyChanged(nameof(IsEmptyState));
        }
    }

    private void RefreshCollections(DiscoveryExecutionResult result)
    {
        ManagedServers.Clear();
        foreach (var s in Topology.ManagedServers)
            ManagedServers.Add(s);

        DiscoveryInsights.Clear();
        foreach (var i in result.Insights)
            DiscoveryInsights.Add(i);

        JvmArguments.Clear();
        foreach (var arg in Topology.JvmArguments)
            JvmArguments.Add(arg);

        PatchInventory.Clear();
        foreach (var p in OracleInventory.Patches)
            PatchInventory.Add(p);

        DiscoveryWarnings.Clear();
        foreach (var w in result.Warnings)
            DiscoveryWarnings.Add(w);
        OnPropertyChanged(nameof(HasDiscoveryWarnings));

        foreach (var stage in result.Stages)
        {
            if (!DiscoveryStages.Any(s => s.Stage == stage.Stage))
                DiscoveryStages.Add(stage);
        }
    }

    public override void ApplyToMigrationConfiguration(MigrationConfiguration config)
    {
        config.Source.MiddlewareHome = MiddlewareHome;
        config.Source.DomainHome     = DomainHome;
        config.Topology              = Topology;
        config.FormsMetadata         = FormsMetadata;
        config.OracleInventory       = OracleInventory;
        config.DiscoveryInsights     = DiscoveryInsights.ToList();
    }

    public override Task OnNavigatingToAsync(MigrationConfiguration config)
    {
        _sessionConfig = config;
        Topology       = config.Topology;
        FormsMetadata  = config.FormsMetadata;
        OracleInventory = config.OracleInventory;
        MiddlewareHome = config.Source.MiddlewareHome ?? MiddlewareHome;
        DomainHome     = config.Source.DomainHome ?? DomainHome;

        ManagedServers.Clear();
        foreach (var s in config.Topology.ManagedServers)
            ManagedServers.Add(s);

        DiscoveryInsights.Clear();
        foreach (var i in config.DiscoveryInsights)
            DiscoveryInsights.Add(i);

        PatchInventory.Clear();
        foreach (var p in config.OracleInventory.Patches)
            PatchInventory.Add(p);

        DiscoveryWarnings.Clear();
        foreach (var w in config.DiscoveryWarnings)
            DiscoveryWarnings.Add(w);
        OnPropertyChanged(nameof(HasDiscoveryWarnings));

        DiscoveryStages.Clear();
        foreach (var stage in config.DiscoveryStages)
            DiscoveryStages.Add(stage);

        ScanModeDisplay = config.DiscoveryUsedRealScan ? "Real environment scan" : "Assessment preview";
        ScanStatusMessage = config.DiscoveryCompleted
            ? $"Discovery results loaded ({config.DiscoveryDurationMs} ms). Re-run to refresh."
            : "Run environment discovery to analyze real middleware topology and Forms / Reports inventory.";

        StatusBannerKind = config.DiscoveryCompleted ? "Success" : "Info";
        DiscoveryProgress = config.DiscoveryCompleted ? 100 : 0;

        OnPropertyChanged(nameof(HasDiscoveryResults));
        OnPropertyChanged(nameof(IsEmptyState));
        return Task.CompletedTask;
    }
}
