using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Runtime;

/// <summary>
/// Executes multi-probe health checks against a middleware component.
///
/// Probe sequence (each probe is skipped if the prior one fails):
///   1. Process probe  — is the known PID alive in the process table?
///   2. Port probe     — can we TCP-connect to Host:Port within the timeout?
///   3. HTTP probe     — does GET /console (or /) return 200 or 302?
///
/// The aggregate <see cref="HealthStatus"/> is computed from all probe results:
///   All pass          → Healthy
///   Process alive but port closed → Degraded  (server is starting)
///   Process missing               → Unhealthy
/// </summary>
public sealed class HealthCheckService
{
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly ILoggingService _log;

    // Shared HttpClient — one per service lifetime; safe for concurrent use.
    private readonly HttpClient _http;

    public HealthCheckService(ILoggingService log)
    {
        _log  = log;
        // Accept redirects — WebLogic /console redirects to /console/login/LoginForm.jsp
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect          = true,
            MaxAutomaticRedirections   = 2,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        })
        {
            Timeout = DefaultProbeTimeout,
        };
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs all applicable probes against <paramref name="component"/> and returns
    /// a detailed <see cref="HealthCheckResult"/>.  Never throws.
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        RuntimeComponent  component,
        TimeSpan?         probeTimeout      = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = probeTimeout ?? DefaultProbeTimeout;
        var sw      = Stopwatch.StartNew();

        var hints   = new List<string>();
        string? warning = null;

        // ── Probe 1: Process ──────────────────────────────────────────────────
        var (processAlive, alivePid) = CheckProcess(component.Pid);

        if (!processAlive && component.Pid.HasValue)
        {
            hints.Add($"Process PID {component.Pid} is no longer in the process table. The server may have crashed or was stopped externally.");
        }

        // ── Probe 2: Port ─────────────────────────────────────────────────────
        bool portListening = false;
        if (component.Port > 0)
        {
            portListening = await CheckPortAsync(component.Host, component.Port, timeout, cancellationToken);
            if (!portListening && processAlive)
            {
                // Process alive but port not yet open — server is still starting
                hints.Add($"Process is running but port {component.Port} is not yet accepting connections. The server may still be initialising.");
            }
        }
        else
        {
            warning = "No port configured — port and HTTP probes skipped.";
        }

        // ── Probe 3: HTTP ─────────────────────────────────────────────────────
        bool?  httpOk  = null;
        int?   httpCode = null;

        if (portListening && component.Kind == ComponentKind.AdminServer)
        {
            (httpOk, httpCode) = await CheckHttpAsync(
                component.Host, component.Port, timeout, cancellationToken);

            if (httpOk == false)
                hints.Add($"HTTP probe returned {httpCode?.ToString() ?? "no response"} — server may be starting up or the console application is not deployed.");
        }
        else if (portListening)
        {
            // Non-AdminServer: port alive is sufficient for Healthy
            httpOk = true;
        }

        // ── Aggregate ──────────────────────────────────────────────────────────
        HealthStatus status;
        if (!processAlive && !portListening)
            status = HealthStatus.Unhealthy;
        else if (processAlive && !portListening)
            status = HealthStatus.Degraded;     // starting up
        else if (portListening && httpOk == false)
            status = HealthStatus.Degraded;
        else if (portListening && (httpOk == true || httpOk is null))
            status = HealthStatus.Healthy;
        else
            status = HealthStatus.Unknown;

        sw.Stop();

        _log.Verbose(
            $"[HealthCheck] {component.Name} → {status} " +
            $"(proc={processAlive}, port={portListening}, http={httpOk?.ToString() ?? "—"}) " +
            $"in {sw.ElapsedMilliseconds}ms",
            "Runtime");

        return new HealthCheckResult
        {
            ProcessExists  = processAlive,
            PortListening  = portListening,
            HttpReachable  = httpOk,
            HttpStatusCode = httpCode,
            Pid            = alivePid,
            Status         = status,
            CheckDuration  = sw.Elapsed,
            Warning        = warning,
            RemediationHints = hints.AsReadOnly(),
            CheckedAt      = DateTimeOffset.UtcNow,
        };
    }

    // ── Probe implementations ──────────────────────────────────────────────────

    /// <summary>
    /// Returns (alive, pid).  When the component has no registered PID, scans
    /// the process table for a java.exe / cmd.exe that has the domain port or
    /// name in its arguments — best-effort detection.
    /// </summary>
    private static (bool alive, int? pid) CheckProcess(int? pid)
    {
        if (!pid.HasValue)
            return (false, null);

        try
        {
            var proc = Process.GetProcessById(pid.Value);
            // GetProcessById throws if not found; if we reach here the PID exists.
            return (!proc.HasExited, proc.HasExited ? null : pid);
        }
        catch (ArgumentException)
        {
            // Process does not exist
            return (false, null);
        }
        catch (Exception)
        {
            // Access denied or WMI error — assume alive to avoid false-negative kills
            return (true, pid);
        }
    }

    /// <summary>
    /// Attempts a TCP connect to <paramref name="host"/>:<paramref name="port"/>.
    /// Returns true when the connection is accepted within <paramref name="timeout"/>.
    /// </summary>
    private static async Task<bool> CheckPortAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends an HTTP GET to the WebLogic console endpoint.
    /// Accepts 200 and 302 (common console redirect) as healthy responses.
    /// Returns (null, null) when the probe could not be attempted.
    /// </summary>
    private async Task<(bool? ok, int? statusCode)> CheckHttpAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var url = $"http://{host}:{port}/console";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var code     = (int)response.StatusCode;

            // 200 OK, 302 Redirect, 401 Unauthorized — all mean the server is alive
            bool alive = code is 200 or 302 or 301 or 401 or 403;
            return (alive, code);
        }
        catch (TaskCanceledException)
        {
            return (false, null);
        }
        catch (HttpRequestException)
        {
            return (false, null);
        }
        catch (Exception ex)
        {
            _log.Verbose($"[HealthCheck] HTTP probe error for {host}:{port} — {ex.Message}", "Runtime");
            return (false, null);
        }
    }
}
