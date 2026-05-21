using CommunityToolkit.Mvvm.ComponentModel;
using WEDM.Domain.Models;

namespace WEDM.UI.ViewModels.Runtime;

/// <summary>
/// Observable ViewModel wrapper for a single <see cref="RuntimeComponent"/>.
/// Populated and updated by <see cref="RuntimeDashboardViewModel"/> on each refresh.
/// </summary>
public sealed partial class RuntimeComponentViewModel : ObservableObject
{
    // ── Source model ──────────────────────────────────────────────────────────

    /// <summary>The underlying domain model; refreshed in-place by the service.</summary>
    public RuntimeComponent Source { get; }

    // ── Identity (init-only, bound as OneWay) ─────────────────────────────────

    public string DomainName  => Source.DomainName;
    public string Name        => Source.Name;
    public string KindDisplay => Source.Kind switch
    {
        ComponentKind.AdminServer  => "Admin Server",
        ComponentKind.ManagedServer => "Managed Server",
        ComponentKind.NodeManager  => "Node Manager",
        ComponentKind.OHS          => "OHS",
        _                          => "Unknown",
    };

    // ── Live state (observable, refreshed via NotifyAll) ──────────────────────

    [ObservableProperty] private RuntimeState  _state;
    [ObservableProperty] private HealthStatus  _health;
    [ObservableProperty] private int?          _pid;
    [ObservableProperty] private int           _port;
    [ObservableProperty] private string        _uptimeDisplay   = "—";
    [ObservableProperty] private string        _statusMessage   = string.Empty;
    [ObservableProperty] private string        _stateDisplay    = "Unknown";
    [ObservableProperty] private string        _healthDisplay   = "—";
    [ObservableProperty] private string        _pidDisplay      = "—";
    [ObservableProperty] private bool          _isRunning;
    [ObservableProperty] private bool          _isStopped;
    [ObservableProperty] private bool          _isFailed;

    public string ConsoleUrl  => Source.ConsoleUrl;
    public string Host        => Source.Host;
    public string DomainHome  => Source.DomainHome;

    public RuntimeComponentViewModel(RuntimeComponent source)
    {
        Source = source;
        Refresh();
    }

    /// <summary>
    /// Pulls the latest values from the underlying <see cref="Source"/> model.
    /// Called on every health-check cycle; safe to call from any thread via Dispatcher.
    /// </summary>
    public void Refresh()
    {
        State         = Source.State;
        Health        = Source.Health;
        Pid           = Source.Pid;
        Port          = Source.Port;
        UptimeDisplay = Source.UptimeDisplay;
        StatusMessage = Source.StatusMessage ?? string.Empty;

        StateDisplay = Source.State switch
        {
            RuntimeState.Unknown    => "Unknown",
            RuntimeState.Starting   => "Starting…",
            RuntimeState.Running    => "Running",
            RuntimeState.Stopping   => "Stopping…",
            RuntimeState.Stopped    => "Stopped",
            RuntimeState.Failed     => "Failed",
            RuntimeState.Recovering => "Recovering…",
            RuntimeState.Unhealthy  => "Unhealthy",
            _                       => "—",
        };

        HealthDisplay = Source.Health switch
        {
            HealthStatus.Healthy   => "● Healthy",
            HealthStatus.Degraded  => "◐ Degraded",
            HealthStatus.Unhealthy => "○ Unhealthy",
            _                      => "— Unknown",
        };

        PidDisplay = Source.Pid?.ToString() ?? "—";

        IsRunning = Source.State is RuntimeState.Running;
        IsStopped = Source.State is RuntimeState.Stopped or RuntimeState.Failed or RuntimeState.Unknown;
        IsFailed  = Source.State is RuntimeState.Failed or RuntimeState.Unhealthy;
    }
}
