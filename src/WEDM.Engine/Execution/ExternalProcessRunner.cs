using System.Diagnostics;
using System.Management;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Execution;

/// <summary>
/// Production-grade external process runner.
///
/// Correctness guarantees
/// ──────────────────────
/// • Stdout and stderr are drained via BeginOutputReadLine / BeginErrorReadLine on separate
///   I/O threads so the child can never block on a full pipe buffer (classic deadlock fix).
/// • stdin is never redirected — avoids the child blocking waiting for EOF on its input.
/// • WaitForExitAsync(ct) + WaitForExit() (no-args) ensures all async callbacks have
///   completed before we examine the output collections.
/// • The process tree is killed (entireProcessTree=true) on cancellation, timeout, or watchdog.
///
/// Telemetry contract
/// ──────────────────
/// Every invocation emits structured <c>[ProcessLaunch]</c> log lines:
///   [ProcessLaunch] {Label} starting: {FileName} {Arguments}
///   [ProcessLaunch] {Label} PID={pid}
///   [ProcessLaunch] {Label} child '{name}' confirmed under PID={pid}   (optional)
///   [ProcessLaunch] {Label} PID={pid} exited with code {n} in {elapsed}
/// </summary>
public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    private readonly ILoggingService _log;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ErrorReceived;

    public ExternalProcessRunner(ILoggingService log) => _log = log;

    // ── Public entry point ────────────────────────────────────────────────────

    public async Task<ExternalProcessResult> RunAsync(
        ExternalProcessOptions options,
        Action<string>?        onStdout          = null,
        Action<string>?        onStderr          = null,
        CancellationToken      cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.FileName))
            throw new ArgumentException("FileName must not be empty.", nameof(options));

        var sw          = Stopwatch.StartNew();
        var outputLines = new List<string>();
        var errorLines  = new List<string>();
        var outputLock  = new object();
        var errorLock   = new object();
        long lastActivityTicks = DateTime.UtcNow.Ticks; // used by watchdog

        // ── 1. Telemetry: log intent ─────────────────────────────────────────
        _log.Info(
            $"[ProcessLaunch] {options.Label} starting: {options.FileName} {options.Arguments}",
            "ProcessRunner");

        // ── 2. Build ProcessStartInfo ────────────────────────────────────────
        var psi = new ProcessStartInfo
        {
            FileName               = options.FileName,
            Arguments              = options.Arguments,
            UseShellExecute        = false,   // required for pipe redirection
            RedirectStandardOutput = true,    // async async pipe drain
            RedirectStandardError  = true,    // async pipe drain
            RedirectStandardInput  = false,   // never redirect stdin — avoids child EOF hang
            CreateNoWindow         = true,    // no console window in WPF host process
            WorkingDirectory       = options.WorkingDirectory ?? Directory.GetCurrentDirectory(),
        };

        // Merge caller-supplied environment overrides into the inherited process environment.
        if (options.EnvironmentVariables is not null)
            foreach (var (k, v) in options.EnvironmentVariables)
                psi.Environment[k] = v;

        // ── 3. Linked CTS: user cancellation + total timeout ─────────────────
        using var totalCts = BuildTotalTimeoutCts(cancellationToken, options.TotalTimeout);

        // ── 4. Watchdog CTS (fires on silent-process detection) ──────────────
        using var watchdogCts = new CancellationTokenSource();

        // ── 5. Combined CTS for WaitForExitAsync ─────────────────────────────
        //    Fires on: user cancel  OR  total timeout  OR  watchdog
        using var allCts = CancellationTokenSource.CreateLinkedTokenSource(
            totalCts.Token, watchdogCts.Token);

        // ── 6. Create process object ─────────────────────────────────────────
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // ── 7. Wire async output handlers BEFORE Start() ────────────────────
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Volatile.Write(ref lastActivityTicks, DateTime.UtcNow.Ticks);
            lock (outputLock) outputLines.Add(e.Data);
            try { OutputReceived?.Invoke(this, e.Data); } catch { /* event handler must not abort I/O thread */ }
            onStdout?.Invoke(e.Data);
            _log.ScriptOutput(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Volatile.Write(ref lastActivityTicks, DateTime.UtcNow.Ticks);
            lock (errorLock) errorLines.Add(e.Data);
            try { ErrorReceived?.Invoke(this, e.Data); } catch { /* idem */ }
            onStderr?.Invoke(e.Data);
            _log.ScriptOutput(e.Data, isError: true);
        };

        // ── 8. Start ─────────────────────────────────────────────────────────
        bool started;
        try
        {
            started = process.Start();
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error(
                $"[ProcessLaunch] {options.Label} Process.Start() threw: {ex.Message}", ex, "ProcessRunner");
            return ExternalProcessResult.FromLaunchFailure(
                $"Process.Start threw: {ex.Message}", sw.Elapsed);
        }

        if (!started)
        {
            sw.Stop();
            var msg = $"Process.Start() returned false for '{options.FileName}' — OS refused to create process.";
            _log.Error($"[ProcessLaunch] {options.Label} — {msg}", null, "ProcessRunner");
            return ExternalProcessResult.FromLaunchFailure(msg, sw.Elapsed);
        }

        var pid = process.Id;
        var launchTime = DateTime.UtcNow;
        _log.Info($"[ProcessLaunch] {options.Label} PID={pid}", "ProcessRunner");

        // ── 9. Begin async pipe reading ──────────────────────────────────────
        //    CRITICAL: must be called before WaitForExitAsync, otherwise the pipe
        //    buffer fills up and the child blocks (deadlock).
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // ── 10. Start watchdog (background task) ─────────────────────────────
        Task? watchdogTask = null;
        if (options.EnableWatchdog
            && options.SilentProcessTimeout != Timeout.InfiniteTimeSpan
            && options.SilentProcessTimeout.TotalMilliseconds > 0)
        {
            watchdogTask = RunWatchdogAsync(
                pid,
                options,
                () => Volatile.Read(ref lastActivityTicks),
                watchdogCts,
                totalCts.Token);
        }

        // ── 11. Optional: launch verification (child process check) ──────────
        if (!string.IsNullOrWhiteSpace(options.ExpectedChildProcessName))
        {
            bool childFound = await VerifyChildLaunchedAsync(
                pid,
                options.ExpectedChildProcessName,
                launchTime,
                options.LaunchVerificationTimeout,
                totalCts.Token).ConfigureAwait(false);

            if (!childFound)
            {
                watchdogCts.Cancel();
                KillProcessTree(process, pid, options.Label);
                process.WaitForExit(5_000);
                sw.Stop();

                List<string> outSnap, errSnap;
                lock (outputLock) outSnap = [.. outputLines];
                lock (errorLock)  errSnap = [.. errorLines];

                var reason = $"Expected child process '{options.ExpectedChildProcessName}' did not appear "
                           + $"within {options.LaunchVerificationTimeout.TotalSeconds:F0}s of launching PID={pid}.";
                _log.Error($"[ProcessLaunch] {options.Label} — {reason}", null, "ProcessRunner");

                return ExternalProcessResult.FromLaunchFailure(
                    reason, sw.Elapsed, outSnap, errSnap,
                    CaptureDiagnostics(options, pid, outSnap, errSnap, reason, "ChildProcessNotLaunched"));
            }

            _log.Info(
                $"[ProcessLaunch] {options.Label} — child '{options.ExpectedChildProcessName}' confirmed under PID={pid}",
                "ProcessRunner");
        }

        // ── 12. Wait for exit ─────────────────────────────────────────────────
        bool processKilled   = false;
        bool hungByWatchdog  = false;
        bool timedOut        = false;

        try
        {
            await process.WaitForExitAsync(allCts.Token).ConfigureAwait(false);

            // WaitForExitAsync returns once the process handle is signaled, but the
            // async stream readers (BeginOutputReadLine/BeginErrorReadLine) may still be
            // draining the internal pipe buffer.  The no-args WaitForExit() blocks until
            // both the process exit AND all DataReceived callbacks have been invoked.
            process.WaitForExit();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User-requested cancellation
            watchdogCts.Cancel();
            KillProcessTree(process, pid, options.Label);
            process.WaitForExit(5_000);
            sw.Stop();
            _log.Info($"[ProcessLaunch] {options.Label} PID={pid} cancelled by caller.", "ProcessRunner");
            return ExternalProcessResult.FromCancelled(sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            // Either total timeout or watchdog fired.  Determine which.
            hungByWatchdog = watchdogCts.IsCancellationRequested && !totalCts.Token.IsCancellationRequested;
            timedOut       = !hungByWatchdog;
            processKilled  = true;
            KillProcessTree(process, pid, options.Label);
            process.WaitForExit(5_000);
        }

        // ── 13. Stop watchdog ─────────────────────────────────────────────────
        try { watchdogCts.Cancel(); } catch { /* ignore */ }
        if (watchdogTask is not null)
            try { await watchdogTask.ConfigureAwait(false); } catch { /* ignore */ }

        sw.Stop();

        List<string> finalOut, finalErr;
        lock (outputLock) finalOut = [.. outputLines];
        lock (errorLock)  finalErr = [.. errorLines];

        // ── 14. Map terminal conditions to result ─────────────────────────────
        if (hungByWatchdog)
        {
            var diag = CaptureDiagnostics(
                options, pid, finalOut, finalErr,
                $"No stdout/stderr activity for {options.SilentProcessTimeout}",
                "SilentProcessHang");
            _log.Warning(
                $"[ProcessLaunch] {options.Label} PID={pid} classified as HUNG — watchdog fired after {sw.Elapsed}.",
                "ProcessRunner");
            return ExternalProcessResult.FromHung(sw.Elapsed, finalOut, finalErr, diag);
        }

        if (timedOut)
        {
            _log.Warning(
                $"[ProcessLaunch] {options.Label} PID={pid} timed out after {options.TotalTimeout}.",
                "ProcessRunner");
            return ExternalProcessResult.FromTimeout(sw.Elapsed, finalOut, finalErr);
        }

        if (processKilled)
        {
            // Generic abort (shouldn't normally reach here, but safety net)
            return ExternalProcessResult.FromTimeout(sw.Elapsed, finalOut, finalErr);
        }

        var exitCode = process.ExitCode;
        _log.Info(
            $"[ProcessLaunch] {options.Label} PID={pid} exited with code {exitCode} in {sw.Elapsed}.",
            "ProcessRunner");

        return new ExternalProcessResult
        {
            ExitCode    = exitCode,
            Output      = string.Join(Environment.NewLine, finalOut),
            Errors      = string.Join(Environment.NewLine, finalErr),
            OutputLines = finalOut,
            ErrorLines  = finalErr,
            Duration    = sw.Elapsed,
            Pid         = pid,
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Polls the process table until a process whose name matches <paramref name="childName"/>
    /// and whose start time is at or after <paramref name="launchTime"/> is found, or the
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    private static async Task<bool> VerifyChildLaunchedAsync(
        int       parentPid,
        string    childName,
        DateTime  launchTime,
        TimeSpan  timeout,
        CancellationToken cancellationToken)
    {
        _ = parentPid; // retained for future WMI-based parent-chain check
        var deadline = DateTime.UtcNow + timeout;
        var nameNoExt = Path.GetFileNameWithoutExtension(childName);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var procs = Process.GetProcessesByName(nameNoExt);
                foreach (var p in procs)
                {
                    using (p)
                    {
                        try
                        {
                            // Accept any instance that started at or after our launch time.
                            // Using a 2-second margin to cover clock resolution differences.
                            if (p.StartTime >= launchTime.AddSeconds(-2))
                                return true;
                        }
                        catch (Exception)
                        {
                            /* process may have exited between enumeration and StartTime access */
                        }
                    }
                }
            }
            catch (Exception)
            {
                /* GetProcessesByName can fail in constrained environments */
            }

            try { await Task.Delay(2_000, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        return false;
    }

    /// <summary>
    /// Background watchdog task.  Cancels <paramref name="watchdogCts"/> if no stdout/stderr
    /// activity is seen for <see cref="ExternalProcessOptions.SilentProcessTimeout"/>.
    /// </summary>
    private async Task RunWatchdogAsync(
        int                        pid,
        ExternalProcessOptions     options,
        Func<long>                 getLastActivityTicks,
        CancellationTokenSource    watchdogCts,
        CancellationToken          abortToken)
    {
        var silentMs  = (long)options.SilentProcessTimeout.TotalMilliseconds;
        var pollMs    = (int)Math.Min(30_000, silentMs / 3); // check at most 3× per silence window
        pollMs        = Math.Max(pollMs, 5_000);             // but at least every 5 s

        while (!abortToken.IsCancellationRequested && !watchdogCts.IsCancellationRequested)
        {
            try { await Task.Delay(pollMs, abortToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var silentFor = new TimeSpan(DateTime.UtcNow.Ticks - getLastActivityTicks());
            if (silentFor.TotalMilliseconds >= silentMs)
            {
                _log.Warning(
                    $"[Watchdog] {options.Label} PID={pid} has produced no output for "
                    + $"{silentFor.TotalSeconds:F0}s (threshold {options.SilentProcessTimeout.TotalSeconds:F0}s) — firing watchdog.",
                    "ProcessRunner");
                try { watchdogCts.Cancel(); } catch { /* ignore */ }
                return;
            }

            _log.Verbose(
                $"[Watchdog] {options.Label} PID={pid} last activity {silentFor.TotalSeconds:F0}s ago — alive.",
                "ProcessRunner");
        }
    }

    /// <summary>Kill the process tree; log but swallow any exceptions.</summary>
    private void KillProcessTree(Process process, int pid, string label)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            _log.Info($"[ProcessLaunch] {label} PID={pid} — process tree killed.", "ProcessRunner");
        }
        catch (Exception ex)
        {
            _log.Warning($"[ProcessLaunch] {label} PID={pid} — Kill threw: {ex.Message}", "ProcessRunner");
        }
    }

    /// <summary>
    /// Builds a <see cref="CancellationTokenSource"/> that fires on whichever comes first:
    /// <paramref name="userToken"/> cancellation or <paramref name="totalTimeout"/> wall-clock.
    /// </summary>
    private static CancellationTokenSource BuildTotalTimeoutCts(
        CancellationToken userToken,
        TimeSpan          totalTimeout)
    {
        var hasTimeout = totalTimeout.TotalMilliseconds > 0
                      && totalTimeout != Timeout.InfiniteTimeSpan;

        if (hasTimeout && userToken.CanBeCanceled)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken);
            linked.CancelAfter(totalTimeout);
            return linked;
        }

        if (hasTimeout)
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(totalTimeout);
            return cts;
        }

        return userToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(userToken)
            : new CancellationTokenSource();
    }

    /// <summary>Snapshot crash diagnostics at the point of process abort.</summary>
    private ExternalProcessCrashDiagnostics CaptureDiagnostics(
        ExternalProcessOptions options,
        int                    pid,
        IReadOnlyList<string>  outLines,
        IReadOnlyList<string>  errLines,
        string                 waitReason,
        string                 classification)
    {
        var tailCount  = options.DiagnosticsTailLines;
        var stdoutTail = TailLines(outLines, tailCount);
        var stderrTail = TailLines(errLines, tailCount);
        var envSnapshot = BuildEnvironmentSnapshot(options.EnvironmentVariables);
        var processTree = CaptureProcessTree(pid);

        return new ExternalProcessCrashDiagnostics
        {
            CommandLine       = $"{options.FileName} {options.Arguments}",
            WorkingDirectory  = options.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            EnvironmentSnapshot = envSnapshot,
            StdoutTail        = stdoutTail,
            StderrTail        = stderrTail,
            ChildProcessTree  = processTree,
            WaitReason        = waitReason,
            HangClassification = classification,
            CapturedAt        = DateTimeOffset.UtcNow,
        };
    }

    private static string TailLines(IReadOnlyList<string> lines, int n)
    {
        if (lines.Count == 0) return "(no output)";
        var start = Math.Max(0, lines.Count - n);
        return string.Join(Environment.NewLine, lines.Skip(start));
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironmentSnapshot(
        IReadOnlyDictionary<string, string>? overrides)
    {
        // Key inherited variables plus any caller overrides.
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "PATH", "JAVA_HOME", "ORACLE_HOME", "TEMP", "TMP", "USERPROFILE", "COMPUTERNAME" })
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (val is not null) snapshot[key] = val;
        }
        if (overrides is not null)
            foreach (var (k, v) in overrides)
                snapshot[k] = v;
        return snapshot;
    }

    /// <summary>
    /// Walk the process tree rooted at <paramref name="rootPid"/> using WMI.
    /// Returns indented text lines suitable for a crash report.
    /// Fails silently — diagnostics must not throw.
    /// </summary>
    private static IReadOnlyList<string> CaptureProcessTree(int rootPid)
    {
        var lines = new List<string>();
        try { AppendProcessTreeLines(rootPid, lines, depth: 0); }
        catch { lines.Add("(process tree capture failed)"); }
        return lines;
    }

    private static void AppendProcessTreeLines(int pid, List<string> result, int depth)
    {
        var indent = new string(' ', depth * 2);
        try
        {
            var proc = Process.GetProcessById(pid);
            result.Add($"{indent}{proc.ProcessName} (PID={pid})");
        }
        catch
        {
            result.Add($"{indent}(PID={pid} — exited)");
            return;
        }

        try
        {
            // Use WMI to find direct children
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId={pid}");
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["ProcessId"] is uint childPid)
                    AppendProcessTreeLines((int)childPid, result, depth + 1);
            }
        }
        catch
        {
            /* WMI unavailable or access denied — tree is partial */
        }
    }
}
