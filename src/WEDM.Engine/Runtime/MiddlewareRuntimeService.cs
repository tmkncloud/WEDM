using System.Collections.Concurrent;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Runtime;

/// <summary>
/// Main implementation of <see cref="IMiddlewareRuntimeService"/>.
///
/// Responsibilities:
///   • Delegates domain discovery to <see cref="MiddlewareRuntimeDiscovery"/>.
///   • Maintains the live <see cref="Components"/> collection, updated in-place.
///   • Runs health checks via <see cref="HealthCheckService"/> and fires
///     <see cref="ComponentStateChanged"/> on every state or health transition.
///   • Delegates Start / Stop / Restart to <see cref="AdminServerController"/>.
///   • Delegates log streaming to <see cref="LogTailService"/>.
///
/// Thread safety:
///   <see cref="Components"/> is exposed as a snapshot-stable read-only list.
///   Mutations happen on the caller's thread during <see cref="RefreshStateAsync"/>
///   and control operations; <see cref="ComponentStateChanged"/> is fired
///   synchronously on that same thread — callers must marshal to the UI dispatcher.
/// </summary>
public sealed class MiddlewareRuntimeService : IMiddlewareRuntimeService
{
    private readonly MiddlewareRuntimeDiscovery _discovery;
    private readonly AdminServerController      _controller;
    private readonly HealthCheckService         _health;
    private readonly LogTailService             _logTail;
    private readonly ILoggingService            _log;

    // The live component list — replaced atomically on full refresh
    private volatile IReadOnlyList<RuntimeComponent> _components = [];

    public MiddlewareRuntimeService(
        MiddlewareRuntimeDiscovery discovery,
        AdminServerController      controller,
        HealthCheckService         health,
        LogTailService             logTail,
        ILoggingService            log)
    {
        _discovery  = discovery;
        _controller = controller;
        _health     = health;
        _logTail    = logTail;
        _log        = log;
    }

    // ── IMiddlewareRuntimeService ──────────────────────────────────────────────

    /// <inheritdoc/>
    public IReadOnlyList<RuntimeComponent> Components => _components;

    /// <inheritdoc/>
    public event EventHandler<RuntimeComponent>? ComponentStateChanged;

    // ── Discovery ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<DomainRuntimeTopology>> DiscoverDomainsAsync(
        string?           middlewareHome    = null,
        CancellationToken cancellationToken = default)
        => _discovery.DiscoverDomainsAsync(middlewareHome, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<RuntimeComponent>> GetComponentsAsync(
        DomainRuntimeTopology topology,
        CancellationToken     cancellationToken = default)
    {
        var components = _discovery.BuildComponents(topology);

        // Merge into the live collection — preserve any live state for components
        // that were already tracked (e.g. after a refresh).
        MergeComponents(components);

        return Task.FromResult(components);
    }

    // ── State management ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task RefreshStateAsync(
        RuntimeRefreshOptions? options           = null,
        CancellationToken      cancellationToken = default)
    {
        var components = _components;
        if (components.Count == 0)
        {
            _log.Verbose("[RuntimeService] RefreshState called but no components are loaded.", "Runtime");
            return;
        }

        var domainFilter = options?.DomainHome;
        var probeTimeout = options?.ProbeTimeout ?? TimeSpan.FromSeconds(5);

        // Run probes concurrently — one per component
        var tasks = new List<Task>();
        foreach (var component in components)
        {
            if (domainFilter is not null &&
                !component.DomainHome.Equals(domainFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            tasks.Add(RefreshComponentAsync(component, options?.FullProbe ?? true,
                probeTimeout, cancellationToken));
        }

        await Task.WhenAll(tasks);
        _log.Verbose($"[RuntimeService] Refresh complete — {tasks.Count} component(s) probed.", "Runtime");
    }

    // ── Start / Stop / Restart ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<RuntimeControlResult> StartAdminServerAsync(
        RuntimeComponent  component,
        TimeSpan?         startupTimeout    = null,
        CancellationToken cancellationToken = default)
    {
        var before = (component.State, component.Health);
        var result = await _controller.StartAdminServerAsync(component, startupTimeout, cancellationToken);
        FireIfChanged(component, before);
        return result;
    }

    /// <inheritdoc/>
    public async Task<RuntimeControlResult> StopAdminServerAsync(
        RuntimeComponent  component,
        string?           adminUser         = null,
        string?           adminPassword     = null,
        TimeSpan?         gracefulTimeout   = null,
        CancellationToken cancellationToken = default)
    {
        var before = (component.State, component.Health);
        var result = await _controller.StopAdminServerAsync(
            component, adminUser, adminPassword, gracefulTimeout, cancellationToken);
        FireIfChanged(component, before);
        return result;
    }

    /// <inheritdoc/>
    public async Task<RuntimeControlResult> RestartAdminServerAsync(
        RuntimeComponent  component,
        string?           adminUser         = null,
        string?           adminPassword     = null,
        CancellationToken cancellationToken = default)
    {
        var before = (component.State, component.Health);
        var result = await _controller.RestartAdminServerAsync(
            component, adminUser, adminPassword, cancellationToken);
        FireIfChanged(component, before);
        return result;
    }

    // ── Health checks ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        RuntimeComponent  component,
        TimeSpan?         probeTimeout      = null,
        CancellationToken cancellationToken = default)
    {
        var before = (component.State, component.Health);
        var result = await _health.CheckHealthAsync(component, probeTimeout, cancellationToken);
        ApplyHealthResult(component, result);
        FireIfChanged(component, before);
        return result;
    }

    // ── Log streaming ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public IAsyncEnumerable<LogTailEntry> TailLogAsync(
        RuntimeComponent  component,
        CancellationToken cancellationToken = default)
        => _logTail.TailLogAsync(component, cancellationToken);

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a health probe for a single component and applies the result,
    /// firing <see cref="ComponentStateChanged"/> if the state or health changed.
    /// </summary>
    private async Task RefreshComponentAsync(
        RuntimeComponent component,
        bool             fullProbe,
        TimeSpan         probeTimeout,
        CancellationToken ct)
    {
        try
        {
            var before = (component.State, component.Health);

            HealthCheckResult hc;
            if (fullProbe)
            {
                hc = await _health.CheckHealthAsync(component, probeTimeout, ct);
            }
            else
            {
                // Process-only probe — skip TCP + HTTP
                hc = await _health.CheckHealthAsync(
                    component,
                    probeTimeout: TimeSpan.FromSeconds(1),
                    cancellationToken: ct);
            }

            ApplyHealthResult(component, hc);
            component.LastChecked = DateTimeOffset.UtcNow;

            FireIfChanged(component, before);
        }
        catch (Exception ex)
        {
            _log.Verbose($"[RuntimeService] Probe failed for {component.Name}: {ex.Message}", "Runtime");
        }
    }

    /// <summary>
    /// Applies a <see cref="HealthCheckResult"/> to the mutable component state fields.
    /// Does NOT fire the change event — the caller decides when to raise it.
    /// </summary>
    private static void ApplyHealthResult(RuntimeComponent component, HealthCheckResult hc)
    {
        component.Health = hc.Status;
        component.Pid    = hc.Pid ?? component.Pid;

        // Update RuntimeState based on health probe outcome
        component.State = (hc.Status, component.State) switch
        {
            // All probes passed — server is running
            (HealthStatus.Healthy, _) => RuntimeState.Running,

            // Process alive but port not yet open — server is still starting
            (HealthStatus.Degraded, RuntimeState.Starting) => RuntimeState.Starting,
            (HealthStatus.Degraded, _)                     => RuntimeState.Unhealthy,

            // Process and port both gone
            (HealthStatus.Unhealthy, RuntimeState.Stopping or RuntimeState.Stopped) => RuntimeState.Stopped,
            (HealthStatus.Unhealthy, _) => RuntimeState.Failed,

            // Unknown / mid-transition states — leave unchanged
            _ => component.State,
        };

        if (hc.RemediationHints.Count > 0)
            component.StatusMessage = hc.RemediationHints[0];
    }

    /// <summary>
    /// Merges a freshly discovered component list into the live collection,
    /// preserving existing runtime state where component identity matches.
    /// </summary>
    private void MergeComponents(IReadOnlyList<RuntimeComponent> fresh)
    {
        var existing = new ConcurrentDictionary<string, RuntimeComponent>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var c in _components)
            existing[ComponentKey(c)] = c;

        foreach (var c in fresh)
        {
            var key = ComponentKey(c);
            if (existing.TryGetValue(key, out var live))
            {
                // Carry forward live state into the freshly-built component shell
                c.State         = live.State;
                c.Health        = live.Health;
                c.Pid           = live.Pid;
                c.StartedAt     = live.StartedAt;
                c.StatusMessage = live.StatusMessage;
            }
        }

        // Atomically replace the live collection
        _components = fresh;
    }

    private static string ComponentKey(RuntimeComponent c)
        => $"{c.DomainHome}|{c.Name}|{c.Kind}";

    /// <summary>
    /// Fires <see cref="ComponentStateChanged"/> when the component's state or
    /// health has changed since <paramref name="before"/> was captured.
    /// </summary>
    private void FireIfChanged(
        RuntimeComponent component,
        (RuntimeState state, HealthStatus health) before)
    {
        if (component.State != before.state || component.Health != before.health)
            ComponentStateChanged?.Invoke(this, component);
    }
}
