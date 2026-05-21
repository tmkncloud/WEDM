using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

// ═══════════════════════════════════════════════════════════════════════════════
// Middleware Runtime Management Models
// ═══════════════════════════════════════════════════════════════════════════════
//
// These models power the WEDM Runtime Management subsystem — the control plane
// for starting, stopping, monitoring, and diagnosing live Oracle Middleware
// components (AdminServer, NodeManager, OHS, ManagedServers).
//
// Design principles:
//   • State is modelled explicitly — no boolean flags, no string states.
//   • HealthCheckResult is immutable; RuntimeComponent is mutable (observable).
//   • DomainRuntimeTopology is the discovery output — built once per scan.
//   • RuntimeControlResult carries the full audit trail for every operation.
// ═══════════════════════════════════════════════════════════════════════════════

// ─────────────────────────────────────────────────────────────────────────────
// State enumerations
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Observable runtime state of a single middleware component.
/// Transitions are driven by health checks and control operations.
/// </summary>
public enum RuntimeState
{
    /// <summary>State cannot be determined — initial state before first probe.</summary>
    Unknown,

    /// <summary>Startup has been initiated; process is launching.</summary>
    Starting,

    /// <summary>Process is running and health checks are passing.</summary>
    Running,

    /// <summary>Graceful shutdown is in progress.</summary>
    Stopping,

    /// <summary>Process is not running (cleanly stopped or never started).</summary>
    Stopped,

    /// <summary>Process crashed, exited unexpectedly, or startup timed out.</summary>
    Failed,

    /// <summary>WEDM is attempting an automatic restart after a failure.</summary>
    Recovering,

    /// <summary>Process is running but health checks are failing (hung JVM, port not listening).</summary>
    Unhealthy
}

/// <summary>Category of a managed middleware component.</summary>
public enum ComponentKind
{
    /// <summary>WebLogic Administration Server (weblogic.Server -name AdminServer).</summary>
    AdminServer,

    /// <summary>WebLogic Managed Server.</summary>
    ManagedServer,

    /// <summary>WebLogic NodeManager daemon.</summary>
    NodeManager,

    /// <summary>Oracle HTTP Server (OHS/httpd).</summary>
    OHS,

    /// <summary>Unclassified component.</summary>
    Unknown
}

/// <summary>Aggregate health rating from a multi-probe health check.</summary>
public enum HealthStatus
{
    /// <summary>No health check has been performed yet.</summary>
    Unknown,

    /// <summary>All probes pass — process, port, and HTTP.</summary>
    Healthy,

    /// <summary>Some probes pass but non-critical issues detected.</summary>
    Degraded,

    /// <summary>Critical probes failing — component is non-functional.</summary>
    Unhealthy
}

// ─────────────────────────────────────────────────────────────────────────────
// Core component model
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Live, observable snapshot of a single middleware component's runtime state.
///
/// Instances are held in <c>IMiddlewareRuntimeService.Components</c> and updated
/// in-place by the health-check loop.  The ViewModel layer binds to these properties.
/// </summary>
public sealed class RuntimeComponent
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Server name as declared in config.xml (e.g. "AdminServer").</summary>
    public string        Name       { get; init; } = string.Empty;

    /// <summary>Functional category of this component.</summary>
    public ComponentKind Kind       { get; init; }

    /// <summary>WebLogic domain name this component belongs to.</summary>
    public string        DomainName { get; init; } = string.Empty;

    /// <summary>Absolute path to the domain home directory.</summary>
    public string        DomainHome { get; init; } = string.Empty;

    // ── Connectivity ─────────────────────────────────────────────────────────

    /// <summary>Listen host (from config.xml listen-address, or "localhost").</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Listen port (from config.xml, or well-known default).</summary>
    public int    Port { get; set; }

    // ── Live state ────────────────────────────────────────────────────────────

    /// <summary>Current runtime state.</summary>
    public RuntimeState  State      { get; set; } = RuntimeState.Unknown;

    /// <summary>PID of the running process, if known.</summary>
    public int?          Pid        { get; set; }

    /// <summary>Health check result from the most recent probe.</summary>
    public HealthStatus  Health     { get; set; } = HealthStatus.Unknown;

    /// <summary>UTC time when the process was last confirmed to have started.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>How long the process has been running (null when not started).</summary>
    [JsonIgnore]
    public TimeSpan? Uptime => StartedAt.HasValue
        ? DateTimeOffset.UtcNow - StartedAt.Value
        : null;

    /// <summary>Human-readable uptime string (e.g. "2h 14m 08s").</summary>
    [JsonIgnore]
    public string UptimeDisplay
    {
        get
        {
            var up = Uptime;
            if (up is null) return "—";
            if (up.Value.TotalDays >= 1) return $"{(int)up.Value.TotalDays}d {up.Value.Hours}h {up.Value.Minutes}m";
            if (up.Value.TotalHours >= 1) return $"{(int)up.Value.TotalHours}h {up.Value.Minutes}m {up.Value.Seconds}s";
            if (up.Value.TotalMinutes >= 1) return $"{up.Value.Minutes}m {up.Value.Seconds}s";
            return $"{up.Value.Seconds}s";
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>Human-readable status message (last error, last action, etc.).</summary>
    public string?        StatusMessage { get; set; }

    /// <summary>UTC time when the state was last refreshed.</summary>
    public DateTimeOffset LastChecked   { get; set; } = DateTimeOffset.UtcNow;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// <summary>Absolute path to the primary log file for this component.</summary>
    public string LogFile     { get; init; } = string.Empty;

    /// <summary>Absolute path to the startup script (startWebLogic.cmd etc.).</summary>
    public string StartScript { get; init; } = string.Empty;

    // ── Console URL ───────────────────────────────────────────────────────────

    /// <summary>WebLogic console URL (only relevant for AdminServer).</summary>
    [JsonIgnore]
    public string ConsoleUrl => Kind == ComponentKind.AdminServer
        ? $"http://{Host}:{Port}/console"
        : string.Empty;

    public override string ToString()
        => $"{Name}({Kind}, {State}, PID={Pid?.ToString() ?? "—"}, {Host}:{Port})";
}

// ─────────────────────────────────────────────────────────────────────────────
// Domain topology discovery
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Discovered topology of a single WebLogic domain.
/// Built by <c>MiddlewareRuntimeDiscovery</c> from config.xml and the process table.
/// </summary>
public sealed class DomainRuntimeTopology
{
    // ── Identity ──────────────────────────────────────────────────────────────

    [JsonPropertyName("domainName")]
    public string DomainName  { get; init; } = string.Empty;

    [JsonPropertyName("domainHome")]
    public string DomainHome  { get; init; } = string.Empty;

    // ── AdminServer ───────────────────────────────────────────────────────────

    [JsonPropertyName("adminServerName")]
    public string AdminServerName { get; init; } = "AdminServer";

    [JsonPropertyName("adminHost")]
    public string AdminHost   { get; init; } = "localhost";

    [JsonPropertyName("adminPort")]
    public int    AdminPort   { get; init; } = 7001;

    // ── Managed servers ───────────────────────────────────────────────────────

    [JsonPropertyName("managedServers")]
    public IReadOnlyList<ManagedServerEntry> ManagedServers { get; init; } = [];

    // ── NodeManager ───────────────────────────────────────────────────────────

    [JsonPropertyName("hasNodeManager")]
    public bool    HasNodeManager   { get; init; }

    [JsonPropertyName("nodeManagerHome")]
    public string? NodeManagerHome  { get; init; }

    [JsonPropertyName("nodeManagerPort")]
    public int     NodeManagerPort  { get; init; } = 5556;

    // ── OHS ───────────────────────────────────────────────────────────────────

    [JsonPropertyName("hasOHS")]
    public bool    HasOHS    { get; init; }

    [JsonPropertyName("ohsHome")]
    public string? OhsHome   { get; init; }

    [JsonPropertyName("ohsPort")]
    public int     OhsPort   { get; init; }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [JsonPropertyName("webLogicVersion")]
    public string? WebLogicVersion { get; init; }

    [JsonPropertyName("discoveredAt")]
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Warnings produced during discovery (missing files, parse errors, etc.).</summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A single managed server entry within a domain topology.</summary>
public sealed class ManagedServerEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; init; } = "localhost";

    [JsonPropertyName("port")]
    public int    Port { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Health check
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Detailed result of a multi-probe health check.
///
/// Probes are executed in order:
///   1. Process exists (PID alive)
///   2. Port listening (TCP connect)
///   3. HTTP reachable (HTTP GET with timeout)
/// </summary>
public sealed class HealthCheckResult
{
    // ── Probe outcomes ────────────────────────────────────────────────────────

    /// <summary>True when the process PID is found in the process table.</summary>
    public bool    ProcessExists  { get; init; }

    /// <summary>True when a TCP connection to Host:Port succeeds within timeout.</summary>
    public bool    PortListening  { get; init; }

    /// <summary>True/false when HTTP probe completes; null when not attempted.</summary>
    public bool?   HttpReachable  { get; init; }

    /// <summary>HTTP status code returned by the health endpoint, if probed.</summary>
    public int?    HttpStatusCode { get; init; }

    /// <summary>PID confirmed alive during this check (may differ from last-known PID).</summary>
    public int?    Pid            { get; init; }

    // ── Aggregate result ──────────────────────────────────────────────────────

    /// <summary>Computed aggregate health status.</summary>
    public HealthStatus Status { get; init; }

    /// <summary>Total wall-clock duration of all probes.</summary>
    public TimeSpan CheckDuration { get; init; }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>Non-fatal warning (e.g. HTTP probe not attempted because port not open).</summary>
    public string? Warning { get; init; }

    /// <summary>Operator-actionable remediation suggestions.</summary>
    public IReadOnlyList<string> RemediationHints { get; init; } = [];

    /// <summary>UTC timestamp when this check was performed.</summary>
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}

// ─────────────────────────────────────────────────────────────────────────────
// Runtime control
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Audit-quality result of a Start / Stop / Restart operation.
/// Every field is populated regardless of success or failure.
/// </summary>
public sealed class RuntimeControlResult
{
    /// <summary>True when the operation completed successfully.</summary>
    public bool    Succeeded   { get; init; }

    /// <summary>Name of the operation: "Start", "Stop", "Restart".</summary>
    public string  Operation   { get; init; } = string.Empty;

    /// <summary>Component name this operation targeted.</summary>
    public string  Component   { get; init; } = string.Empty;

    /// <summary>Wall-clock duration of the entire operation.</summary>
    public TimeSpan Duration   { get; init; }

    /// <summary>Error message when <see cref="Succeeded"/> is false.</summary>
    public string? Error       { get; init; }

    /// <summary>Runtime state observed at the end of the operation.</summary>
    public RuntimeState FinalState { get; init; }

    /// <summary>Stdout lines captured from the control script or process.</summary>
    public IReadOnlyList<string> OutputLines { get; init; } = [];

    /// <summary>Stderr lines captured from the control script or process.</summary>
    public IReadOnlyList<string> ErrorLines  { get; init; } = [];

    /// <summary>UTC timestamp when the operation was initiated.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public override string ToString()
        => $"{Operation} {Component}: {(Succeeded ? "OK" : "FAILED")} in {Duration.TotalSeconds:F1}s → {FinalState}";
}

// ─────────────────────────────────────────────────────────────────────────────
// Log tailing
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A single line emitted by the log-tail service.</summary>
public sealed class LogTailEntry
{
    /// <summary>Wall-clock time when the line was observed (not parsed from the log timestamp).</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Raw log line text.</summary>
    public string Line     { get; init; } = string.Empty;

    /// <summary>True when the line came from stderr or contains ERROR/FATAL markers.</summary>
    public bool   IsError  { get; init; }

    /// <summary>Source component name (AdminServer, NodeManager, etc.).</summary>
    public string Source   { get; init; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// Refresh request / result
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Request parameters for a runtime state refresh.
/// Controls which components are probed and at what depth.
/// </summary>
public sealed class RuntimeRefreshOptions
{
    /// <summary>When null, refresh all discovered domains; otherwise target a specific domain home.</summary>
    public string? DomainHome { get; init; }

    /// <summary>When true, perform full health probes (port + HTTP); when false, process-only probe.</summary>
    public bool    FullProbe  { get; init; } = true;

    /// <summary>Timeout for each individual health probe.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
