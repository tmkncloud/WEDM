using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Runtime control plane for Oracle Middleware components managed by WEDM.
///
/// Responsibilities:
///   • Discover all WebLogic domains and their component topology from the filesystem.
///   • Build and maintain the live <see cref="RuntimeComponent"/> collection.
///   • Execute Start / Stop / Restart operations with full audit trails.
///   • Run health checks (process, port, HTTP) and update component state.
///   • Stream live log output from AdminServer, NodeManager, and OHS log files.
///   • Emit <see cref="ComponentStateChanged"/> events for real-time UI binding.
///
/// Safety invariants:
///   • Only processes whose PID is owned by WEDM (or that can be confirmed via
///     command-line analysis to match the targeted domain) will be stopped.
///   • External processes are never automatically terminated.
///   • All operations are cancellation-token aware.
/// </summary>
public interface IMiddlewareRuntimeService
{
    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the filesystem for WebLogic domains under the given middleware home
    /// (or uses the stored <see cref="DeploymentConfiguration"/> when null).
    ///
    /// Returns one <see cref="DomainRuntimeTopology"/> per discovered domain.
    /// Never throws — parse errors are captured in <see cref="DomainRuntimeTopology.Warnings"/>.
    /// </summary>
    Task<IReadOnlyList<DomainRuntimeTopology>> DiscoverDomainsAsync(
        string?           middlewareHome    = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the initial <see cref="RuntimeComponent"/> collection for a specific domain.
    /// Components are returned in logical startup order (NodeManager → AdminServer → Managed → OHS).
    /// </summary>
    Task<IReadOnlyList<RuntimeComponent>> GetComponentsAsync(
        DomainRuntimeTopology topology,
        CancellationToken     cancellationToken = default);

    // ── State management ──────────────────────────────────────────────────────

    /// <summary>
    /// Refreshes the runtime state of all (or targeted) components by running
    /// health probes and updating <see cref="Components"/>.
    ///
    /// Raises <see cref="ComponentStateChanged"/> for each component whose
    /// <see cref="RuntimeComponent.State"/> or <see cref="RuntimeComponent.Health"/> changed.
    /// </summary>
    Task RefreshStateAsync(
        RuntimeRefreshOptions? options           = null,
        CancellationToken      cancellationToken = default);

    /// <summary>
    /// Live collection of all tracked runtime components.
    /// Updated in-place by <see cref="RefreshStateAsync"/> and control operations.
    /// </summary>
    IReadOnlyList<RuntimeComponent> Components { get; }

    // ── Start / Stop / Restart ────────────────────────────────────────────────

    /// <summary>
    /// Starts the AdminServer for the specified component.
    ///
    /// Implementation:
    ///   1. Verify the component is currently Stopped or Failed.
    ///   2. Execute <c>{domainHome}/bin/startWebLogic.cmd</c> as a detached process.
    ///   3. Register the PID with <c>IOracleProcessLifecycleService</c>.
    ///   4. Poll health checks until Running or <paramref name="startupTimeout"/> expires.
    ///   5. Return audit result with full output capture.
    /// </summary>
    Task<RuntimeControlResult> StartAdminServerAsync(
        RuntimeComponent  component,
        TimeSpan?         startupTimeout    = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the AdminServer gracefully, escalating to force-kill on timeout.
    ///
    /// Implementation:
    ///   1. Try WLST online shutdown('AdminServer', 'Server', block='true').
    ///   2. If WLST times out or fails, use <c>IOracleProcessLifecycleService.ShutdownAsync</c>.
    ///   3. Verify the process has exited.
    ///   4. Return audit result.
    /// </summary>
    Task<RuntimeControlResult> StopAdminServerAsync(
        RuntimeComponent  component,
        string?           adminUser         = null,
        string?           adminPassword     = null,
        TimeSpan?         gracefulTimeout   = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops then restarts the AdminServer.
    /// Uses Stop then Start with the same timeout parameters.
    /// </summary>
    Task<RuntimeControlResult> RestartAdminServerAsync(
        RuntimeComponent  component,
        string?           adminUser         = null,
        string?           adminPassword     = null,
        CancellationToken cancellationToken = default);

    // ── Health checks ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a multi-probe health check against the specified component.
    ///
    /// Probes performed (in order):
    ///   1. Process check — is the PID in the process table?
    ///   2. Port probe   — can we TCP-connect to Host:Port?
    ///   3. HTTP probe   — does GET /console (or equivalent) return 200/302?
    ///
    /// Probe 3 is skipped when Probe 2 fails.
    /// Returns a <see cref="HealthCheckResult"/> regardless of outcome.
    /// </summary>
    Task<HealthCheckResult> CheckHealthAsync(
        RuntimeComponent  component,
        TimeSpan?         probeTimeout      = null,
        CancellationToken cancellationToken = default);

    // ── Log streaming ─────────────────────────────────────────────────────────

    /// <summary>
    /// Streams log lines from the component's primary log file.
    ///
    /// Behaviour:
    ///   • Starts reading from the current end-of-file (tail mode, not full replay).
    ///   • Reads new lines incrementally as they are written.
    ///   • Rotation-safe: if the log file is recreated, the stream follows the new file.
    ///   • Cancellation-safe: cancelling <paramref name="cancellationToken"/> stops the stream cleanly.
    ///   • Returns an empty stream when the log file does not exist.
    /// </summary>
    IAsyncEnumerable<LogTailEntry> TailLogAsync(
        RuntimeComponent  component,
        CancellationToken cancellationToken = default);

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised whenever a component's <see cref="RuntimeComponent.State"/> or
    /// <see cref="RuntimeComponent.Health"/> changes.
    ///
    /// Always raised on the calling thread context — callers must marshal to the
    /// UI dispatcher if needed (the ViewModel layer handles this).
    /// </summary>
    event EventHandler<RuntimeComponent>? ComponentStateChanged;
}
