using NotificationKind = WEDM.UI.ViewModels.Runtime.RuntimeNotificationViewModel.NotificationKind;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Runtime;

namespace WEDM.UI.ViewModels.Runtime;

/// <summary>
/// ViewModel for the Enterprise Runtime Operations Console.
///
/// Responsibilities:
///   • Domain discovery and component grid population
///   • 10-second auto-refresh with live state probing
///   • Start / Stop / Restart operations with feedback and audit
///   • Filter/search across all components
///   • Summary counts (Running / Stopped / Failed / Degraded)
///   • Transient notification ring (last 50 items)
///   • Hosts <see cref="RuntimeDetailPanelViewModel"/> for the right-side detail pane
///
/// Safety invariant: only WEDM-owned PIDs are stopped. External processes are never terminated.
/// </summary>
public sealed partial class RuntimeDashboardViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(10);
    private const int MaxNotifications = 50;

    private readonly IMiddlewareRuntimeService _runtime;
    private readonly ILoggingService           _log;

    private System.Threading.Timer? _refreshTimer;
    private bool                    _refreshRunning;
    private bool                    _disposed;

    // ── Observable state ──────────────────────────────────────────────────────

    /// <summary>Raw component collection — bind to <see cref="ComponentsView"/> for filtered display.</summary>
    public ObservableCollection<RuntimeComponentViewModel> Components { get; } = [];

    /// <summary>Filtered / sortable view of <see cref="Components"/> — bind DataGrid ItemsSource to this.</summary>
    public ICollectionView ComponentsView { get; }

    /// <summary>Transient notification ring (newest first, capped at 50).</summary>
    public ObservableCollection<RuntimeNotificationViewModel> Notifications { get; } = [];

    /// <summary>Owns the right-side detail panel.</summary>
    public RuntimeDetailPanelViewModel DetailPanel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedComponent))]
    private RuntimeComponentViewModel? _selectedComponent;

    [ObservableProperty] private bool   _isRefreshing;
    [ObservableProperty] private bool   _isDiscovering;
    [ObservableProperty] private bool   _isDiscovered;
    [ObservableProperty] private bool   _isOperationRunning;
    [ObservableProperty] private string _statusLine           = "Ready — click Discover to scan domains.";
    [ObservableProperty] private string _lastOperationMessage = string.Empty;
    [ObservableProperty] private bool   _lastOperationSucceeded;
    [ObservableProperty] private bool   _autoRefreshEnabled   = true;
    [ObservableProperty] private string _adminUser            = string.Empty;
    [ObservableProperty] private string _adminPassword        = string.Empty;
    [ObservableProperty] private string _filterText           = string.Empty;

    // ── Summary counts ────────────────────────────────────────────────────────

    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _stoppedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _degradedCount;
    [ObservableProperty] private int _totalCount;

    // ── Computed ──────────────────────────────────────────────────────────────

    public bool HasSelectedComponent => SelectedComponent is not null;

    // ── Constructor ───────────────────────────────────────────────────────────

    public RuntimeDashboardViewModel(
        IMiddlewareRuntimeService runtime,
        ILoggingService           log)
    {
        _runtime = runtime;
        _log     = log;

        // Build the ICollectionView for filtered/sorted DataGrid binding
        ComponentsView        = CollectionViewSource.GetDefaultView(Components);
        ComponentsView.Filter = FilterComponent;

        // Create detail panel wired to the runtime service's log tail
        DetailPanel = new RuntimeDetailPanelViewModel(
            (component, ct) => _runtime.TailLogAsync(component, ct));

        _runtime.ComponentStateChanged += OnComponentStateChanged;

        // Start auto-refresh timer (first tick after the interval)
        _refreshTimer = new System.Threading.Timer(
            _ => _ = AutoRefreshTickAsync(),
            null,
            AutoRefreshInterval,
            AutoRefreshInterval);
    }

    // ── Property change hooks ─────────────────────────────────────────────────

    partial void OnSelectedComponentChanged(RuntimeComponentViewModel? value)
    {
        DetailPanel.SetComponent(value);
        StartComponentCommand.NotifyCanExecuteChanged();
        StopComponentCommand.NotifyCanExecuteChanged();
        RestartComponentCommand.NotifyCanExecuteChanged();
    }

    partial void OnFilterTextChanged(string value)
    {
        ComponentsView.Refresh();
    }

    // ── Commands ───────────────────────────────────────────────────────────────

    /// <summary>Discovers all WebLogic domains and builds the component grid.</summary>
    [RelayCommand]
    private async Task DiscoverAsync()
    {
        IsDiscovering = true;
        IsRefreshing  = true;
        StatusLine    = "Scanning for WebLogic domains…";
        Components.Clear();
        UpdateCounts();

        try
        {
            var topologies = await _runtime.DiscoverDomainsAsync();
            foreach (var topo in topologies)
            {
                var comps = await _runtime.GetComponentsAsync(topo);
                foreach (var c in comps)
                    Components.Add(new RuntimeComponentViewModel(c));
            }

            UpdateCounts();
            IsDiscovered = Components.Count > 0;

            if (Components.Count == 0)
            {
                StatusLine = "No WebLogic domains found. Verify the middleware home path.";
                PushNotification("No domains discovered. Check middleware home configuration.", NotificationKind.Warning);
            }
            else
            {
                StatusLine = $"Discovered {topologies.Count} domain(s) — {Components.Count} component(s).";
                PushNotification($"Discovery complete: {topologies.Count} domain(s), {Components.Count} component(s).", NotificationKind.Success);
            }

            // Run initial health check
            await _runtime.RefreshStateAsync();
            SyncAllComponents();
        }
        catch (Exception ex)
        {
            StatusLine = $"Discovery failed: {ex.Message}";
            _log.Error("[RuntimeDashboard] Discovery error", ex, "Runtime");
            PushNotification($"Discovery failed: {ex.Message}", NotificationKind.Error);
        }
        finally
        {
            IsDiscovering = false;
            IsRefreshing  = false;
        }
    }

    /// <summary>Refreshes runtime state of all discovered components.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Components.Count == 0) return;
        if (_refreshRunning)       return;
        _refreshRunning = true;
        IsRefreshing    = true;

        try
        {
            await _runtime.RefreshStateAsync();
            SyncAllComponents();
            StatusLine = $"Refreshed — {DateTimeOffset.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _log.Error("[RuntimeDashboard] Refresh error", ex, "Runtime");
        }
        finally
        {
            IsRefreshing    = false;
            _refreshRunning = false;
        }
    }

    /// <summary>Starts the selected AdminServer.</summary>
    [RelayCommand(CanExecute = nameof(CanStartComponent))]
    private async Task StartComponentAsync()
    {
        if (SelectedComponent is null) return;
        await RunControlOperationAsync(SelectedComponent,
            async c => await _runtime.StartAdminServerAsync(c.Source), "Start");
    }

    private bool CanStartComponent()
        => SelectedComponent is { IsRunning: false } &&
           SelectedComponent.Source.Kind == ComponentKind.AdminServer;

    /// <summary>Stops the selected AdminServer.</summary>
    [RelayCommand(CanExecute = nameof(CanStopComponent))]
    private async Task StopComponentAsync()
    {
        if (SelectedComponent is null) return;
        await RunControlOperationAsync(SelectedComponent,
            async c => await _runtime.StopAdminServerAsync(
                c.Source,
                adminUser:     string.IsNullOrWhiteSpace(AdminUser)     ? null : AdminUser,
                adminPassword: string.IsNullOrWhiteSpace(AdminPassword) ? null : AdminPassword),
            "Stop");
    }

    private bool CanStopComponent()
        => SelectedComponent is { IsRunning: true } &&
           SelectedComponent.Source.Kind == ComponentKind.AdminServer;

    /// <summary>Restarts the selected AdminServer.</summary>
    [RelayCommand(CanExecute = nameof(CanRestartComponent))]
    private async Task RestartComponentAsync()
    {
        if (SelectedComponent is null) return;
        await RunControlOperationAsync(SelectedComponent,
            async c => await _runtime.RestartAdminServerAsync(
                c.Source,
                adminUser:     string.IsNullOrWhiteSpace(AdminUser)     ? null : AdminUser,
                adminPassword: string.IsNullOrWhiteSpace(AdminPassword) ? null : AdminPassword),
            "Restart");
    }

    private bool CanRestartComponent()
        => SelectedComponent is not null &&
           SelectedComponent.Source.Kind == ComponentKind.AdminServer;

    /// <summary>Clears all notifications from the ring.</summary>
    [RelayCommand]
    private void ClearNotifications() => Notifications.Clear();

    /// <summary>Toggles the auto-refresh timer.</summary>
    [RelayCommand]
    private void ToggleAutoRefresh()
    {
        AutoRefreshEnabled = !AutoRefreshEnabled;
        StatusLine = AutoRefreshEnabled
            ? "Auto-refresh enabled (10s interval)."
            : "Auto-refresh paused.";
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task RunControlOperationAsync(
        RuntimeComponentViewModel vm,
        Func<RuntimeComponentViewModel, Task<RuntimeControlResult>> operation,
        string operationName)
    {
        IsOperationRunning   = true;
        LastOperationMessage = $"{operationName} {vm.Name}…";
        LastOperationSucceeded = false;

        try
        {
            var result = await operation(vm);

            LastOperationSucceeded = result.Succeeded;
            LastOperationMessage   = result.Succeeded
                ? $"{operationName} {vm.Name}: completed in {result.Duration.TotalSeconds:F1}s."
                : $"{operationName} {vm.Name}: {result.Error ?? "operation failed"}.";

            // Record in detail panel history
            DetailPanel.RecordOperation(result);

            // Push notification
            var kind = result.Succeeded ? NotificationKind.Success : NotificationKind.Error;
            PushNotification(LastOperationMessage, kind, vm.Name);

            Dispatch(() =>
            {
                vm.Refresh();
                UpdateCounts();
            });

            StartComponentCommand.NotifyCanExecuteChanged();
            StopComponentCommand.NotifyCanExecuteChanged();
            RestartComponentCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            LastOperationSucceeded = false;
            LastOperationMessage   = $"{operationName} failed: {ex.Message}";
            PushNotification(LastOperationMessage, NotificationKind.Error, vm.Name);
            _log.Error($"[RuntimeDashboard] {operationName} error for {vm.Name}", ex, "Runtime");
        }
        finally
        {
            IsOperationRunning = false;
        }
    }

    private async Task AutoRefreshTickAsync()
    {
        if (!AutoRefreshEnabled || _refreshRunning || _disposed) return;
        await RefreshAsync();
    }

    private void SyncAllComponents()
    {
        Dispatch(() =>
        {
            foreach (var vm in Components)
                vm.Refresh();
            UpdateCounts();
        });
    }

    private void UpdateCounts()
    {
        RunningCount  = Components.Count(c => c.State == RuntimeState.Running);
        StoppedCount  = Components.Count(c => c.State is RuntimeState.Stopped or RuntimeState.Unknown);
        FailedCount   = Components.Count(c => c.State is RuntimeState.Failed or RuntimeState.Unhealthy);
        DegradedCount = Components.Count(c => c.Health == HealthStatus.Degraded);
        TotalCount    = Components.Count;
    }

    private bool FilterComponent(object obj)
    {
        if (obj is not RuntimeComponentViewModel vm) return true;
        var f = FilterText.Trim();
        if (string.IsNullOrEmpty(f)) return true;
        return vm.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || vm.DomainName.Contains(f, StringComparison.OrdinalIgnoreCase)
            || vm.KindDisplay.Contains(f, StringComparison.OrdinalIgnoreCase)
            || vm.StateDisplay.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void PushNotification(string message, NotificationKind kind, string source = "Runtime")
    {
        Dispatch(() =>
        {
            if (Notifications.Count >= MaxNotifications)
                Notifications.RemoveAt(Notifications.Count - 1);
            Notifications.Insert(0, new RuntimeNotificationViewModel(message, kind, source));
        });
    }

    private void OnComponentStateChanged(object? sender, RuntimeComponent changed)
    {
        Dispatch(() =>
        {
            var vm = Components.FirstOrDefault(v =>
                v.Name == changed.Name &&
                string.Equals(v.DomainName, changed.DomainName, StringComparison.OrdinalIgnoreCase));

            vm?.Refresh();
            UpdateCounts();

            StartComponentCommand.NotifyCanExecuteChanged();
            StopComponentCommand.NotifyCanExecuteChanged();
            RestartComponentCommand.NotifyCanExecuteChanged();
        });
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _runtime.ComponentStateChanged -= OnComponentStateChanged;
        DetailPanel.LogViewer.Dispose();
    }
}


