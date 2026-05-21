using System.Diagnostics;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Runtime;

/// <summary>
/// Executes Start / Stop / Restart operations against a WebLogic AdminServer.
///
/// Start strategy:
///   Launches <c>{domainHome}/bin/startWebLogic.cmd</c> as a detached process using
///   <see cref="Process"/> directly (NOT <see cref="IExternalProcessRunner"/>, which
///   awaits process exit and would block forever against a long-running server daemon).
///   Polls <see cref="HealthCheckService"/> until the server is healthy or the startup
///   timeout expires.
///
/// Stop strategy:
///   Stage 1 — WLST online: connect + shutdown('AdminServer','Server',block='true').
///   Stage 2 — Force kill: locate Oracle process by domain home and call
///              <see cref="IOracleProcessLifecycleService.ShutdownAsync"/>.
///
/// Safety invariant: only processes whose command-line or working directory references
/// the specific domain home are ever targeted — no arbitrary java.exe kills.
/// </summary>
public sealed class AdminServerController
{
    private static readonly TimeSpan DefaultStartupTimeout  = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DefaultGracefulTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval           = TimeSpan.FromSeconds(5);

    private readonly ILoggingService               _log;
    private readonly IExternalProcessRunner        _runner;    // for WLST stop script only
    private readonly IOracleProcessLifecycleService _lifecycle;
    private readonly HealthCheckService            _health;

    public AdminServerController(
        ILoggingService                log,
        IExternalProcessRunner         runner,
        IOracleProcessLifecycleService lifecycle,
        HealthCheckService             health)
    {
        _log       = log;
        _runner    = runner;
        _lifecycle = lifecycle;
        _health    = health;
    }

    // ── Start ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the AdminServer daemon, then polls health checks until Running or
    /// <paramref name="startupTimeout"/> expires.
    /// </summary>
    public async Task<RuntimeControlResult> StartAdminServerAsync(
        RuntimeComponent  component,
        TimeSpan?         startupTimeout    = null,
        CancellationToken cancellationToken = default)
    {
        var sw      = Stopwatch.StartNew();
        var timeout = startupTimeout ?? DefaultStartupTimeout;

        var output = new List<string>();
        var errors = new List<string>();

        _log.Info($"[AdminServerController] Starting {component.Name} in domain {component.DomainName}", "Runtime");

        if (!File.Exists(component.StartScript))
        {
            var msg = $"Start script not found: {component.StartScript}";
            component.State         = RuntimeState.Failed;
            component.StatusMessage = msg;
            return FailResult("Start", component.Name, sw.Elapsed, msg, RuntimeState.Failed);
        }

        component.State         = RuntimeState.Starting;
        component.StatusMessage = "Launching startWebLogic.cmd…";

        Process? proc = null;
        try
        {
            // Fire-and-forget daemon launch — UseShellExecute=false so we can set the
            // working directory; CreateNoWindow=true suppresses the cmd window.
            var psi = new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                Arguments              = $"/c \"{component.StartScript}\"",
                WorkingDirectory       = component.DomainHome,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            proc = new Process { StartInfo = psi };

            // Drain pipes asynchronously — necessary to prevent pipe-buffer deadlock.
            // Output naturally slows once WebLogic starts writing to its own log file.
            proc.OutputDataReceived += (_, e) =>
            { if (e.Data is not null) lock (output) output.Add(e.Data); };
            proc.ErrorDataReceived += (_, e) =>
            { if (e.Data is not null) lock (errors) errors.Add(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            component.Pid       = proc.Id;
            component.StartedAt = DateTimeOffset.UtcNow;

            _log.Info($"[AdminServerController] {component.Name} process started (PID {proc.Id})", "Runtime");
            output.Add($"Process started — PID {proc.Id}.");
        }
        catch (Exception ex)
        {
            component.State         = RuntimeState.Failed;
            component.StatusMessage = $"Launch failed: {ex.Message}";
            _log.Error($"[AdminServerController] Failed to launch {component.Name}", ex, "Runtime");
            return FailResult("Start", component.Name, sw.Elapsed, ex.Message, RuntimeState.Failed);
        }

        // ── Poll health until Running or deadline ──────────────────────────────
        using var deadline = new CancellationTokenSource(timeout);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, linked.Token); }
            catch (OperationCanceledException) { break; }

            // Check for premature exit
            try
            {
                if (proc.HasExited)
                {
                    var exitMsg = $"Process exited unexpectedly (code {proc.ExitCode}).";
                    component.State         = RuntimeState.Failed;
                    component.StatusMessage = exitMsg;
                    _log.Warning($"[AdminServerController] {component.Name} process exited early: {exitMsg}", "Runtime");
                    return FailResult("Start", component.Name, sw.Elapsed, exitMsg,
                        RuntimeState.Failed, Snapshot(output), Snapshot(errors));
                }
            }
            catch { /* HasExited can throw if handle was released — treat as still running */ }

            var hc = await _health.CheckHealthAsync(
                component, probeTimeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken);

            if (hc.Status == HealthStatus.Healthy)
            {
                component.State         = RuntimeState.Running;
                component.Health        = HealthStatus.Healthy;
                component.Pid           = hc.Pid ?? component.Pid;
                component.StatusMessage = $"Running — startup completed in {sw.Elapsed.TotalSeconds:F1}s.";
                _log.Info($"[AdminServerController] {component.Name} is Running (PID {component.Pid})", "Runtime");

                return new RuntimeControlResult
                {
                    Succeeded   = true,
                    Operation   = "Start",
                    Component   = component.Name,
                    Duration    = sw.Elapsed,
                    FinalState  = RuntimeState.Running,
                    OutputLines = Snapshot(output),
                    ErrorLines  = Snapshot(errors),
                    StartedAt   = DateTimeOffset.UtcNow - sw.Elapsed,
                };
            }
        }

        // ── Timeout or outer cancellation ──────────────────────────────────────
        bool timedOut = deadline.IsCancellationRequested;
        var finalMsg  = timedOut
            ? $"Startup timeout after {timeout.TotalMinutes:F0} min — server may still be initialising."
            : "Start operation was cancelled.";

        component.State         = timedOut ? RuntimeState.Unhealthy : RuntimeState.Unknown;
        component.StatusMessage = finalMsg;
        _log.Warning($"[AdminServerController] {component.Name}: {finalMsg}", "Runtime");

        return FailResult("Start", component.Name, sw.Elapsed, finalMsg,
            component.State, Snapshot(output), Snapshot(errors));
    }

    // ── Stop ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gracefully shuts down the AdminServer, escalating to force-kill on failure.
    /// </summary>
    public async Task<RuntimeControlResult> StopAdminServerAsync(
        RuntimeComponent  component,
        string?           adminUser         = null,
        string?           adminPassword     = null,
        TimeSpan?         gracefulTimeout   = null,
        CancellationToken cancellationToken = default)
    {
        var sw      = Stopwatch.StartNew();
        var timeout = gracefulTimeout ?? DefaultGracefulTimeout;

        var output = new List<string>();
        var errors = new List<string>();

        _log.Info($"[AdminServerController] Stopping {component.Name} in domain {component.DomainName}", "Runtime");

        component.State         = RuntimeState.Stopping;
        component.StatusMessage = "Stopping…";

        // ── Stage 1: WLST graceful shutdown ───────────────────────────────────
        bool wlstOk = false;
        if (!string.IsNullOrWhiteSpace(adminUser) && !string.IsNullOrWhiteSpace(adminPassword))
        {
            wlstOk = await TryWlstShutdownAsync(
                component, adminUser, adminPassword, timeout, output, errors, cancellationToken);
        }
        else
        {
            output.Add("No admin credentials supplied — skipping WLST graceful shutdown.");
        }

        // ── Stage 2: Force stop via process lifecycle ──────────────────────────
        if (!wlstOk)
        {
            output.Add("Attempting force stop via Oracle process lifecycle service.");
            await ForceStopAsync(component, output, errors, cancellationToken);
        }

        // ── Final health check ─────────────────────────────────────────────────
        var finalHc  = await _health.CheckHealthAsync(
            component, probeTimeout: TimeSpan.FromSeconds(5),
            cancellationToken: cancellationToken);

        bool stopped = !finalHc.ProcessExists && !finalHc.PortListening;

        component.State         = stopped ? RuntimeState.Stopped  : RuntimeState.Failed;
        component.Health        = stopped ? HealthStatus.Unknown   : HealthStatus.Unhealthy;
        component.Pid           = stopped ? null                   : component.Pid;
        component.StartedAt     = stopped ? null                   : component.StartedAt;
        component.StatusMessage = stopped ? "Stopped." : "Stop attempt completed — process may still be running.";

        _log.Info($"[AdminServerController] {component.Name} stop → {component.State}", "Runtime");

        return new RuntimeControlResult
        {
            Succeeded   = stopped,
            Operation   = "Stop",
            Component   = component.Name,
            Duration    = sw.Elapsed,
            FinalState  = component.State,
            Error       = stopped ? null : "Process still detectable after stop attempt.",
            OutputLines = output.AsReadOnly(),
            ErrorLines  = errors.AsReadOnly(),
            StartedAt   = DateTimeOffset.UtcNow - sw.Elapsed,
        };
    }

    // ── Restart ────────────────────────────────────────────────────────────────

    /// <summary>Stops then restarts the AdminServer.</summary>
    public async Task<RuntimeControlResult> RestartAdminServerAsync(
        RuntimeComponent  component,
        string?           adminUser         = null,
        string?           adminPassword     = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        _log.Info($"[AdminServerController] Restarting {component.Name}", "Runtime");

        var stopResult = await StopAdminServerAsync(
            component, adminUser, adminPassword, cancellationToken: cancellationToken);

        if (!stopResult.Succeeded)
        {
            return new RuntimeControlResult
            {
                Succeeded   = false,
                Operation   = "Restart",
                Component   = component.Name,
                Duration    = sw.Elapsed,
                FinalState  = component.State,
                Error       = $"Stop phase failed: {stopResult.Error}",
                OutputLines = stopResult.OutputLines,
                ErrorLines  = stopResult.ErrorLines,
                StartedAt   = DateTimeOffset.UtcNow - sw.Elapsed,
            };
        }

        // Brief pause to let OS release port bindings
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        var startResult = await StartAdminServerAsync(component, cancellationToken: cancellationToken);

        return new RuntimeControlResult
        {
            Succeeded   = startResult.Succeeded,
            Operation   = "Restart",
            Component   = component.Name,
            Duration    = sw.Elapsed,
            FinalState  = startResult.FinalState,
            Error       = startResult.Succeeded ? null : startResult.Error,
            OutputLines = [..stopResult.OutputLines, ..startResult.OutputLines],
            ErrorLines  = [..stopResult.ErrorLines,  ..startResult.ErrorLines],
            StartedAt   = DateTimeOffset.UtcNow - sw.Elapsed,
        };
    }

    // ── WLST graceful shutdown ─────────────────────────────────────────────────

    private async Task<bool> TryWlstShutdownAsync(
        RuntimeComponent component,
        string adminUser, string adminPassword,
        TimeSpan timeout,
        List<string> output, List<string> errors,
        CancellationToken ct)
    {
        try
        {
            var wlstCmd = FindWlstCmd(component.DomainHome);
            if (wlstCmd is null)
            {
                output.Add("wlst.cmd not found — WLST graceful shutdown skipped.");
                _log.Warning($"[AdminServerController] wlst.cmd not found for {component.DomainHome}", "Runtime");
                return false;
            }

            var tmpScript = Path.Combine(Path.GetTempPath(), $"wedm-stop-{Guid.NewGuid():N}.py");
            try
            {
                // Jython 2.x syntax (WLST uses Jython 2 — must not use 'as' keyword)
                var url    = $"t3://{component.Host}:{component.Port}";
                var script = string.Join(Environment.NewLine,
                    "try:",
                    $"    connect('{EscapeWlst(adminUser)}', '{EscapeWlst(adminPassword)}', '{url}')",
                    $"    shutdown('{EscapeWlst(component.Name)}', 'Server', block='true')",
                    "    disconnect()",
                    "except Exception, e:",
                    "    print('WLST stop error: ' + str(e))",
                    "    import sys",
                    "    sys.exit(1)");

                await File.WriteAllTextAsync(tmpScript, script, ct);
                output.Add($"Executing WLST online shutdown via {Path.GetFileName(wlstCmd)}.");

                var opts = new ExternalProcessOptions
                {
                    FileName               = "cmd.exe",
                    Arguments              = $"/c \"{wlstCmd}\" \"{tmpScript}\"",
                    WorkingDirectory       = component.DomainHome,
                    TotalTimeout           = timeout + TimeSpan.FromSeconds(15),
                    SilentProcessTimeout   = Timeout.InfiniteTimeSpan,   // WLST may be quiet
                    EnableWatchdog         = false,
                    Label                  = $"WLST-Stop-{component.Name}",
                };

                var result = await _runner.RunAsync(
                    opts,
                    onStdout: line => lock (output) output.Add(line),
                    onStderr: line => lock (errors) errors.Add(line),
                    cancellationToken: ct);

                if (result.Success)
                {
                    output.Add("WLST shutdown completed successfully.");
                    _log.Info($"[AdminServerController] WLST graceful shutdown succeeded for {component.Name}", "Runtime");
                    return true;
                }

                output.Add($"WLST exited with code {result.ExitCode}.");
                _log.Warning($"[AdminServerController] WLST shutdown exit {result.ExitCode} for {component.Name}", "Runtime");
                return false;
            }
            finally
            {
                try { File.Delete(tmpScript); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"WLST shutdown exception: {ex.Message}");
            _log.Warning($"[AdminServerController] WLST stop exception for {component.Name}: {ex.Message}", "Runtime");
            return false;
        }
    }

    // ── Force stop via process lifecycle ──────────────────────────────────────

    private async Task ForceStopAsync(
        RuntimeComponent component,
        List<string> output, List<string> errors,
        CancellationToken ct)
    {
        try
        {
            var oracle = _lifecycle.DetectOracleProcesses()
                .Where(p => MatchesDomain(p, component.DomainHome))
                .ToList();

            if (oracle.Count == 0)
            {
                output.Add("No Oracle processes found matching this domain home — possibly already stopped.");
                return;
            }

            output.Add($"Found {oracle.Count} Oracle process(es) — initiating staged shutdown.");

            var results = await _lifecycle.ShutdownAsync(oracle, ShutdownPolicy.Default, ct);
            foreach (var r in results)
                output.Add($"  PID {r.ProcessId} ({r.ProcessName}): {r.Stage}{(r.Error is not null ? " — " + r.Error : string.Empty)}");
        }
        catch (Exception ex)
        {
            errors.Add($"Force stop error: {ex.Message}");
            _log.Error($"[AdminServerController] Force stop failed for {component.Name}", ex, "Runtime");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks up from the domain home looking for wlst.cmd under common MW_HOME layouts.
    /// Returns null when not found (caller falls through to force kill).
    /// </summary>
    private static string? FindWlstCmd(string domainHome)
    {
        var dir = domainHome;
        for (var i = 0; i < 6; i++)
        {
            dir = Path.GetDirectoryName(dir);
            if (dir is null) break;

            // 12c / 14c: oracle_common/common/bin/wlst.cmd
            var a = Path.Combine(dir, "oracle_common", "common", "bin", "wlst.cmd");
            if (File.Exists(a)) return a;

            // Older: wlserver/common/bin/wlst.cmd
            var b = Path.Combine(dir, "wlserver", "common", "bin", "wlst.cmd");
            if (File.Exists(b)) return b;

            // Alternative flat layout
            var c = Path.Combine(dir, "common", "bin", "wlst.cmd");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>
    /// Returns true when the detected Oracle process is associated with the given domain home
    /// (by command-line or working directory match).
    /// </summary>
    private static bool MatchesDomain(OracleProcessInfo proc, string domainHome)
    {
        if (proc.CommandLine?.Contains(domainHome, StringComparison.OrdinalIgnoreCase) == true)
            return true;
        if (proc.WorkingDirectory?.StartsWith(domainHome, StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return false;
    }

    /// <summary>Escapes a string for safe embedding inside a WLST/Jython single-quoted literal.</summary>
    private static string EscapeWlst(string value)
        => value.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>Thread-safe snapshot of a list being populated by async output readers.</summary>
    private static IReadOnlyList<string> Snapshot(List<string> list)
    { lock (list) return list.ToList().AsReadOnly(); }

    private static RuntimeControlResult FailResult(
        string op, string component, TimeSpan duration, string error,
        RuntimeState state,
        IReadOnlyList<string>? outputLines = null,
        IReadOnlyList<string>? errorLines  = null)
        => new()
        {
            Succeeded   = false,
            Operation   = op,
            Component   = component,
            Duration    = duration,
            FinalState  = state,
            Error       = error,
            OutputLines = outputLines ?? [],
            ErrorLines  = errorLines  ?? [],
            StartedAt   = DateTimeOffset.UtcNow - duration,
        };
}
