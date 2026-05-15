using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery;
using WEDM.UI.Services;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Migration;

public sealed partial class MigrationDiscoveryViewModel : MigrationWizardStepViewModel
{
    private readonly IMiddlewareDiscoveryOrchestrator _orchestrator;
    private readonly IWedmRuntimeOptions _runtimeOptions;
    private MigrationConfiguration? _sessionConfig;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunDiscovery))]
    [NotifyPropertyChangedFor(nameof(CanRunSimulation))]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _middlewareHome = @"D:\Oracle\Middleware";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunDiscovery))]
    [NotifyPropertyChangedFor(nameof(CanRunSimulation))]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _domainHome = @"D:\Oracle\user_projects\domains\base_domain";

    [ObservableProperty] private string _middlewareHomeError = string.Empty;
    [ObservableProperty] private string _domainHomeError = string.Empty;
    [ObservableProperty] private bool _pathsAreValid;

    [ObservableProperty] private bool _scanInProgress;
    [ObservableProperty] private double _discoveryProgress;
    [ObservableProperty] private string _scanStatusMessage = "Enter valid Middleware and Domain paths, then run a real environment scan.";
    [ObservableProperty] private string _statusBannerKind = "Info";
    [ObservableProperty] private string _scanModeDisplay = "Awaiting scan";
    [ObservableProperty] private bool _isSimulationResult;

    [ObservableProperty] private MiddlewareTopologySnapshot _topology = new();
    [ObservableProperty] private FormsReportsMetadataSnapshot _formsMetadata = new();
    [ObservableProperty] private OracleInventorySnapshot _oracleInventory = new();

    public ObservableCollection<ManagedServerDescriptor> ManagedServers { get; } = [];
    public ObservableCollection<EnvironmentDiscoveryFinding> DiscoveryInsights { get; } = [];
    public ObservableCollection<string> JvmArguments { get; } = [];
    public ObservableCollection<DiscoveryStageResult> DiscoveryStages { get; } = [];
    public ObservableCollection<PatchInventoryRecord> PatchInventory { get; } = [];
    public ObservableCollection<string> DiscoveryWarnings { get; } = [];

    public bool ShowSimulationMode => _runtimeOptions.AllowDiscoverySimulation;

    public bool CanRunDiscovery => PathsAreValid && !ScanInProgress;

    public bool CanRunSimulation => ShowSimulationMode && !ScanInProgress;

    public bool HasRealDiscoveryResults =>
        !IsSimulationResult &&
        Topology.ScanStatus is DiscoveryScanStatus.Completed or DiscoveryScanStatus.Partial;

    public bool HasDiscoveryResults => HasRealDiscoveryResults || IsSimulationResult;

    public bool IsEmptyState => !HasDiscoveryResults && !ScanInProgress;

    public bool HasDiscoveryWarnings => DiscoveryWarnings.Count > 0;

    public override bool CanProceed => HasRealDiscoveryResults && !ScanInProgress;

    public MigrationDiscoveryViewModel(
        IMiddlewareDiscoveryOrchestrator orchestrator,
        IWedmRuntimeOptions runtimeOptions)
    {
        _orchestrator   = orchestrator;
        _runtimeOptions = runtimeOptions;
        _orchestrator.ProgressChanged += OnDiscoveryProgress;

        StepTitle       = "Source Environment Discovery";
        StepDescription = "Read-only scanning of middleware homes, WebLogic domains, Forms/Reports, and patch inventory.";
        StepIcon        = "🔍";

        ValidatePaths();
    }

    partial void OnMiddlewareHomeChanged(string value)
    {
        ValidatePaths();
        InvalidatePriorResultsIfPathsChanged();
    }

    partial void OnDomainHomeChanged(string value)
    {
        ValidatePaths();
        InvalidatePriorResultsIfPathsChanged();
    }

    private void ValidatePaths()
    {
        var result = DiscoveryEnvironmentValidator.Validate(MiddlewareHome, DomainHome);
        MiddlewareHomeError = result.MiddlewareHomeError;
        DomainHomeError     = result.DomainHomeError;
        PathsAreValid       = result.IsValid;

        if (!PathsAreValid && !ScanInProgress)
        {
            ScanStatusMessage = string.IsNullOrEmpty(MiddlewareHomeError)
                ? DomainHomeError
                : string.IsNullOrEmpty(DomainHomeError)
                    ? MiddlewareHomeError
                    : $"{MiddlewareHomeError} {DomainHomeError}";
        }

        RunDiscoveryCommand.NotifyCanExecuteChanged();
        RunSimulationDiscoveryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanProceed));
        OnPropertyChanged(nameof(HasRealDiscoveryResults));
    }

    protected override void RunStepValidation() => ValidatePaths();

    private void InvalidatePriorResultsIfPathsChanged()
    {
        if (_sessionConfig is null || !_sessionConfig.DiscoveryCompleted) return;
        ClearDiscoveryResults();
        ScanModeDisplay   = "Awaiting scan";
        ScanStatusMessage = "Paths changed — run discovery again before continuing.";
        StatusBannerKind  = "Info";
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

    [RelayCommand(CanExecute = nameof(CanRunDiscovery))]
    private async Task RunDiscoveryAsync()
    {
        await ExecuteDiscoveryAsync(simulation: false);
    }

    [RelayCommand(CanExecute = nameof(CanRunSimulation))]
    private async Task RunSimulationDiscoveryAsync()
    {
        await ExecuteDiscoveryAsync(simulation: true);
    }

    private async Task ExecuteDiscoveryAsync(bool simulation)
    {
        if (_sessionConfig is null) return;
        if (!simulation && !PathsAreValid) return;

        var sw = Stopwatch.StartNew();
        try
        {
            ScanInProgress = true;
            IsBusy         = true;
            ClearError();
            DiscoveryStages.Clear();
            DiscoveryWarnings.Clear();
            IsSimulationResult = simulation;
            SetBusy(true, simulation ? "Running simulation preview…" : "Running environment discovery pipeline…");
            StatusBannerKind = "Progress";
            ScanModeDisplay  = simulation ? "SIMULATION MODE" : "REAL ENVIRONMENT SCAN";

            _sessionConfig.Source.MiddlewareHome = MiddlewareHome;
            _sessionConfig.Source.DomainHome     = DomainHome;

            var options = new DiscoveryScanOptions
            {
                MiddlewareHome         = MiddlewareHome,
                DomainHome             = DomainHome,
                AllowSimulatedFallback = simulation,
                ForceSimulation        = simulation,
                ScanTimeoutSeconds     = 120,
            };

            var result = await _orchestrator.ExecuteAsync(_sessionConfig.Source, options);

            if (!simulation && !result.UsedRealScan)
            {
                StatusBannerKind  = "Error";
                ScanStatusMessage = result.Warnings.LastOrDefault()
                    ?? "Discovery failed. Correct the paths and try again.";
                ClearDiscoveryResults();
                return;
            }

            ApplyDiscoveryResult(result, simulation);

            ScanModeDisplay = simulation
                ? "SIMULATION MODE"
                : "REAL ENVIRONMENT SCAN";

            StatusBannerKind = result.ScanStatus switch
            {
                DiscoveryScanStatus.Completed => simulation ? "Warning" : "Success",
                DiscoveryScanStatus.Partial   => "Warning",
                _                             => "Error",
            };

            ScanStatusMessage = simulation
                ? "Simulation complete — illustrative data only. Run a real scan before continuing."
                : $"Discovery finished in {result.TotalDurationMs} ms — {FormsMetadata.FormCount} forms, " +
                  $"{Topology.ManagedServerCount} managed servers, {OracleInventory.Patches.Count} patches.";

            MigrationDiagnostics.TraceTiming("Discovery", sw.ElapsedMilliseconds, ScanModeDisplay);
            MigrationDiagnostics.Trace("DiscoveryMode", result.UsedRealScan ? "RealScan" : simulation ? "Simulation" : "Failed");
        }
        catch (Exception ex)
        {
            StatusBannerKind  = "Error";
            ScanStatusMessage = "Discovery could not complete. Verify paths, permissions, and retry.";
            HandleException(ex, "Discovery");
            ClearDiscoveryResults();
        }
        finally
        {
            ScanInProgress = false;
            IsBusy         = false;
            SetBusy(false);
            DiscoveryProgress = HasDiscoveryResults ? 100 : 0;
            NotifyDiscoveryStateChanged();
        }
    }

    private void ApplyDiscoveryResult(DiscoveryExecutionResult result, bool simulation)
    {
        if (_sessionConfig is null) return;

        Topology        = result.Topology;
        FormsMetadata   = result.FormsMetadata;
        OracleInventory = result.OracleInventory;
        RefreshCollections(result);

        _sessionConfig.Topology            = result.Topology;
        _sessionConfig.FormsMetadata       = result.FormsMetadata;
        _sessionConfig.OracleInventory     = result.OracleInventory;
        _sessionConfig.DomainAnalysis      = result.DomainAnalysis;
        _sessionConfig.DiscoveryInsights   = result.Insights.ToList();
        _sessionConfig.DiscoveryStages     = result.Stages.ToList();
        _sessionConfig.DiscoveryWarnings   = result.Warnings.ToList();
        _sessionConfig.DiscoveryCompleted  = HasRealDiscoveryResults;
        _sessionConfig.DiscoveryUsedRealScan = result.UsedRealScan && !simulation;
        _sessionConfig.DiscoveryDurationMs = result.TotalDurationMs;
        IsSimulationResult = simulation || !result.UsedRealScan;
    }

    private void ClearDiscoveryResults()
    {
        Topology         = new MiddlewareTopologySnapshot();
        FormsMetadata    = new FormsReportsMetadataSnapshot();
        OracleInventory  = new OracleInventorySnapshot();
        IsSimulationResult = false;
        ManagedServers.Clear();
        DiscoveryInsights.Clear();
        JvmArguments.Clear();
        PatchInventory.Clear();
        DiscoveryStages.Clear();

        if (_sessionConfig is not null)
        {
            _sessionConfig.DiscoveryCompleted    = false;
            _sessionConfig.DiscoveryUsedRealScan = false;
        }

        DiscoveryProgress = 0;
        NotifyDiscoveryStateChanged();
    }

    private void NotifyDiscoveryStateChanged()
    {
        OnPropertyChanged(nameof(CanProceed));
        OnPropertyChanged(nameof(HasDiscoveryResults));
        OnPropertyChanged(nameof(HasRealDiscoveryResults));
        OnPropertyChanged(nameof(IsEmptyState));
        OnPropertyChanged(nameof(HasDiscoveryWarnings));
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
        if (!HasRealDiscoveryResults) return;
        config.Topology          = Topology;
        config.FormsMetadata     = FormsMetadata;
        config.OracleInventory   = OracleInventory;
        config.DiscoveryInsights = DiscoveryInsights.ToList();
    }

    public override Task OnNavigatingToAsync(MigrationConfiguration config)
    {
        _sessionConfig = config;
        Topology       = config.Topology;
        FormsMetadata  = config.FormsMetadata;
        OracleInventory = config.OracleInventory;
        MiddlewareHome = config.Source.MiddlewareHome ?? MiddlewareHome;
        DomainHome     = config.Source.DomainHome ?? DomainHome;
        IsSimulationResult = config.DiscoveryCompleted && !config.DiscoveryUsedRealScan;

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

        DiscoveryStages.Clear();
        foreach (var stage in config.DiscoveryStages)
            DiscoveryStages.Add(stage);

        ScanModeDisplay = config.DiscoveryUsedRealScan
            ? "REAL ENVIRONMENT SCAN"
            : config.DiscoveryCompleted
                ? "SIMULATION MODE"
                : "Awaiting scan";

        ScanStatusMessage = config.DiscoveryUsedRealScan
            ? $"Discovery results loaded ({config.DiscoveryDurationMs} ms). Re-run to refresh."
            : config.DiscoveryCompleted
                ? "Simulation results loaded — run a real environment scan before continuing."
                : "Enter valid Middleware and Domain paths, then run a real environment scan.";

        StatusBannerKind = config.DiscoveryUsedRealScan ? "Success"
            : config.DiscoveryCompleted ? "Warning" : "Info";
        DiscoveryProgress = config.DiscoveryCompleted ? 100 : 0;

        ValidatePaths();
        NotifyDiscoveryStateChanged();
        return Task.CompletedTask;
    }
}
