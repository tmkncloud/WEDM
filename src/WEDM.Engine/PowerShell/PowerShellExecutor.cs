using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using WEDM.Domain.Interfaces;

namespace WEDM.Engine.PowerShell;

/// <summary>
/// Production PowerShell executor using the PowerShell SDK for in-process execution
/// and a detected out-of-process host for Administrator-elevated operations.
///
/// Compatibility guarantee
/// ──────────────────────
/// • Works with Windows PowerShell 5.1 (powershell.exe) AND PowerShell 7+ (pwsh.exe).
/// • Does NOT call ImportPSModule() on built-in modules — those modules are compiled
///   into the PS SDK assemblies and have no standalone .psd1 on disk in the PS7 SDK
///   in-process context.  Calling ImportPSModule("Microsoft.PowerShell.Management")
///   throws CmdletInvocationException in PS 7+ and must never be attempted.
/// • InitialSessionState.CreateDefault2() already registers every built-in cmdlet
///   (Set-Location, Get-ChildItem, Start-Process, …) as SessionStateCmdletEntry objects
///   without any file loading — no additional imports are needed.
/// • If CreateDefault2() fails for any reason a safe fallback to CreateDefault() is
///   attempted, and if that also fails the executor enters a graceful no-op mode so
///   the WEDM UI still launches.
///
/// Elevated execution
/// ──────────────────
/// • Prefers pwsh.exe (PS 7+) when detected; falls back to powershell.exe (5.1).
/// • Uses UseShellExecute=true + Verb="runas" for UAC elevation.
/// • Captures output via a temp Tee-Object wrapper script rather than pipe redirection
///   (pipe redirection is incompatible with UseShellExecute=true).
/// </summary>
public sealed class PowerShellExecutor : IPowerShellExecutor, IDisposable
{
    private readonly ILoggingService    _log;
    private readonly RunspacePool?      _pool;          // null when both ISS strategies fail
    private readonly bool               _poolAvailable;
    private readonly PowerShellHostInfo _hostInfo;
    private bool _disposed;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ErrorReceived;

    // ── Constructor ───────────────────────────────────────────────────────────

    public PowerShellExecutor(ILoggingService log)
    {
        _log = log;

        // ── 1. Detect PS edition and preferred elevated executable ────────────
        _hostInfo = PowerShellHostDetector.Detect();
        _log.Info(_hostInfo.ToString(), "PowerShell");

        // ── 2. Build InitialSessionState — NO explicit module imports ────────
        //
        // IMPORTANT: Do NOT call iss.ImportPSModule() for built-in modules.
        //
        // Reason: In the PowerShell 7 SDK (Microsoft.PowerShell.SDK 7.x), built-in
        // cmdlets like Set-Location, Get-ChildItem, Start-Process are registered directly
        // as SessionStateCmdletEntry objects by CreateDefault2() — they are NOT separate
        // .psd1/.psm1 files on disk.  Calling iss.ImportPSModule("Microsoft.PowerShell.Management")
        // asks PS to find a module file that does not exist at any path in $PSModulePath for
        // an SDK-hosted process, resulting in:
        //   CmdletInvocationException: Cannot find the built-in module
        //   'Microsoft.PowerShell.Management' that is compatible with the 'Core' edition.
        //
        // Similarly: Do NOT set PSModuleAutoLoadingPreference = "None".
        // Module auto-loading in an SDK session applies only to EXTERNAL modules in
        // $PSModulePath, not to the built-in cmdlets which are already present.
        // Disabling it would break scripts that import third-party modules.
        //
        // The informational trace "The 'Set-Location' command was found in the module
        // 'Microsoft.PowerShell.Management'..." that appeared in earlier runs was PS's
        // verbose module-discovery tracing, NOT a sign of a real problem.
        var iss = BuildSafeInitialSessionState(out var issStrategy);

        // ── 3. Open runspace pool with graceful double-fallback ───────────────
        _pool          = null;
        _poolAvailable = false;
        var pool       = RunspaceFactory.CreateRunspacePool(iss);

        try
        {
            pool.Open();
            _pool          = pool;
            _poolAvailable = true;
            _log.Info(
                $"[PowerShellHost] In-process runspace pool ready (strategy={issStrategy}).",
                "PowerShell");
        }
        catch (Exception ex1)
        {
            _log.Warning(
                $"[PowerShellHost] {issStrategy} runspace open failed: {ex1.Message} — trying CreateDefault() fallback.",
                "PowerShell");
            try { pool.Dispose(); } catch { /* ignore */ }

            // Fallback: CreateDefault() is an older but more universally compatible factory
            try
            {
                var fallbackIss = InitialSessionState.CreateDefault();
                fallbackIss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
                var fallbackPool = RunspaceFactory.CreateRunspacePool(fallbackIss);
                fallbackPool.Open();
                _pool          = fallbackPool;
                _poolAvailable = true;
                _hostInfo      = _hostInfo with { RestrictedMode = true };
                _log.Warning(
                    "[PowerShellHost] Using CreateDefault() fallback — some advanced PS cmdlets may be unavailable.",
                    "PowerShell");
            }
            catch (Exception ex2)
            {
                // Both strategies failed.  WEDM must still launch; in-process PS execution
                // will return graceful errors at call time rather than crashing at startup.
                _log.Error(
                    $"[PowerShellHost] All runspace strategies failed. In-process PS execution unavailable. " +
                    $"Error1={ex1.Message} Error2={ex2.Message}",
                    null, "PowerShell");
            }
        }
    }

    // ── Public interface methods ──────────────────────────────────────────────

    public async Task<PowerShellResult> ExecuteScriptAsync(
        string scriptPath,
        Dictionary<string, object>? parameters = null,
        string? workingDirectory = null,
        bool runAsAdministrator = false,
        CancellationToken cancellationToken = default,
        TimeSpan? operationTimeout = null)
    {
        if (!File.Exists(scriptPath))
            return PowerShellResult.Fail($"Script not found: {scriptPath}");

        _log.Info($"Executing script: {scriptPath}", "PowerShell");

        if (runAsAdministrator)
            return await ExecuteElevatedAsync(
                scriptPath, parameters, workingDirectory, cancellationToken, operationTimeout)
                .ConfigureAwait(false);

        var escaped = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        return await ExecuteInProcessAsync(
            $"& '{escaped}'", parameters, workingDirectory, cancellationToken, operationTimeout)
            .ConfigureAwait(false);
    }

    public async Task<PowerShellResult> ExecuteCommandAsync(
        string command,
        string? workingDirectory = null,
        bool runAsAdministrator = false,
        CancellationToken cancellationToken = default,
        TimeSpan? operationTimeout = null)
    {
        _log.Debug($"Executing command: {command[..Math.Min(200, command.Length)]}", "PowerShell");

        if (runAsAdministrator)
        {
            var cwd    = workingDirectory ?? Directory.GetCurrentDirectory();
            var cwdEsc = cwd.Replace("'", "''", StringComparison.Ordinal);
            var tmp    = Path.Combine(Path.GetTempPath(), $"wedm_{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(tmp,
                $"Set-Location -LiteralPath '{cwdEsc}'\n{command}",
                cancellationToken).ConfigureAwait(false);
            try
            {
                return await ExecuteElevatedAsync(
                    tmp, null, workingDirectory, cancellationToken, operationTimeout)
                    .ConfigureAwait(false);
            }
            finally { TryDelete(tmp); }
        }

        return await ExecuteInProcessAsync(
            command, null, workingDirectory, cancellationToken, operationTimeout)
            .ConfigureAwait(false);
    }

    public async Task<PowerShellResult> ExecuteModuleFunctionAsync(
        string modulePath,
        string functionName,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default,
        TimeSpan? operationTimeout = null)
    {
        if (!File.Exists(modulePath))
            return PowerShellResult.Fail($"Module not found: {modulePath}");

        var paramBlock = BuildParamBlock(parameters);
        var modEsc     = modulePath.Replace("'", "''", StringComparison.Ordinal);
        var command    = $"Import-Module -Force -LiteralPath '{modEsc}' -ErrorAction Stop; {functionName} {paramBlock}";
        return await ExecuteInProcessAsync(
            command, null, null, cancellationToken, operationTimeout)
            .ConfigureAwait(false);
    }

    // ── In-process execution (RunspacePool) ───────────────────────────────────

    private async Task<PowerShellResult> ExecuteInProcessAsync(
        string command,
        Dictionary<string, object>? parameters,
        string? workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout)
    {
        // Guard: pool unavailable (both ISS strategies failed at startup)
        if (!_poolAvailable || _pool is null)
        {
            _log.Warning(
                "[PowerShellHost] In-process PS execution skipped — runspace pool unavailable. " +
                "Check startup-error.txt for details.", "PowerShell");
            return PowerShellResult.Fail(
                "PowerShell runspace pool is unavailable. Check startup-error.txt for details.");
        }

        var sw          = Stopwatch.StartNew();
        var outputLines = new List<string>();
        var errorLines  = new List<string>();

        using var ps = System.Management.Automation.PowerShell.Create();
        ps.RunspacePool = _pool;

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            ps.AddCommand("Set-Location").AddArgument(workingDirectory).AddStatement();

        ps.AddScript(command);

        if (parameters is not null)
            foreach (var (key, value) in parameters)
                ps.Runspace?.SessionStateProxy?.SetVariable(key, value);

        try
        {
            var outputCollection = new PSDataCollection<PSObject>();
            outputCollection.DataAdded += (_, e) =>
            {
                var line = outputCollection[e.Index]?.ToString() ?? string.Empty;
                outputLines.Add(line);
                OutputReceived?.Invoke(this, line);
                _log.ScriptOutput(line);
            };

            ps.Streams.Error.DataAdded += (_, e) =>
            {
                var line = ps.Streams.Error[e.Index]?.ToString() ?? string.Empty;
                errorLines.Add(line);
                ErrorReceived?.Invoke(this, line);
                _log.ScriptOutput(line, isError: true);
            };

            ps.Streams.Warning.DataAdded += (_, e) =>
                _log.Warning(ps.Streams.Warning[e.Index]?.Message ?? string.Empty, "PowerShell");

            ps.Streams.Verbose.DataAdded += (_, e) =>
                _log.Verbose(ps.Streams.Verbose[e.Index]?.Message ?? string.Empty, "PowerShell");

            var invokeTask = Task.Factory.FromAsync(
                ps.BeginInvoke<PSObject, PSObject>(null, outputCollection),
                ps.EndInvoke);

            var hasDeadline = operationTimeout is { TotalMilliseconds: > 0 }
                           || cancellationToken.CanBeCanceled;

            if (!hasDeadline)
            {
                await invokeTask.ConfigureAwait(false);
            }
            else
            {
                using var linked   = CreateLinkedTimeoutSource(cancellationToken, operationTimeout);
                var gate           = Task.Delay(Timeout.Infinite, linked.Token);
                var completed      = await Task.WhenAny(invokeTask, gate).ConfigureAwait(false);
                if (completed != invokeTask)
                {
                    try { ps.Stop(); } catch { /* ignore */ }
                    sw.Stop();
                    return cancellationToken.IsCancellationRequested
                        ? PowerShellResult.Fail("Execution cancelled.", -1)
                        : PowerShellResult.TimedOutResult(sw.Elapsed);
                }

                await invokeTask.ConfigureAwait(false);
            }

            sw.Stop();

            // ── Extract __WEDM_EXIT sentinel ─────────────────────────────────
            // Scripts that wrap Start-Process emit "__WEDM_EXIT:<code>" so we can read
            // the child exit code without being confused by ps.HadErrors, which fires
            // on any native-command stderr output regardless of actual success.
            int? wedmExplicitExit = null;
            var cleanOutputLines  = outputLines.ToList();
            for (int i = cleanOutputLines.Count - 1; i >= 0; i--)
            {
                var marker = cleanOutputLines[i].Trim();
                if (marker.StartsWith("__WEDM_EXIT:", StringComparison.Ordinal)
                    && int.TryParse(marker["__WEDM_EXIT:".Length..], out var parsedRc))
                {
                    wedmExplicitExit = parsedRc;
                    cleanOutputLines.RemoveAt(i);
                    break;
                }
            }

            bool hadErrors;
            int  exitCode;
            if (wedmExplicitExit.HasValue)
            {
                exitCode  = wedmExplicitExit.Value;
                hadErrors = exitCode != 0;
            }
            else
            {
                // Only fail for ErrorRecord objects that carry an actual exception;
                // bare informational error-stream messages must not abort the step.
                hadErrors = ps.HadErrors && ps.Streams.Error.Any(e => e?.Exception is not null);
                exitCode  = hadErrors ? 1 : 0;
            }

            return new PowerShellResult
            {
                Success     = !hadErrors,
                ExitCode    = exitCode,
                Output      = string.Join(Environment.NewLine, cleanOutputLines),
                Errors      = string.Join(Environment.NewLine, errorLines),
                OutputLines = cleanOutputLines,
                ErrorLines  = errorLines,
                Duration    = sw.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            try { ps.Stop(); } catch { /* ignore */ }
            sw.Stop();
            return cancellationToken.IsCancellationRequested
                ? PowerShellResult.Fail("Execution cancelled.", -1)
                : PowerShellResult.TimedOutResult(sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error("PowerShell in-process execution failed", ex, "PowerShell");
            return PowerShellResult.Fail(ex.Message, 1, ex);
        }
    }

    // ── Elevated out-of-process execution ─────────────────────────────────────

    private async Task<PowerShellResult> ExecuteElevatedAsync(
        string scriptPath,
        Dictionary<string, object>? parameters,
        string? workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout)
    {
        var sw       = Stopwatch.StartNew();
        var outputSb = new StringBuilder();

        var paramArgs = string.Empty;
        if (parameters?.Count > 0)
            paramArgs = " " + string.Join(" ",
                parameters.Select(kv => $"-{kv.Key} \"{kv.Value}\""));

        using CancellationTokenSource? linkedCts =
            operationTimeout is { TotalMilliseconds: > 0 } || cancellationToken.CanBeCanceled
                ? CreateLinkedTimeoutSource(cancellationToken, operationTimeout)
                : null;
        var waitToken = linkedCts?.Token ?? CancellationToken.None;

        // Elevation strategy:
        //   UseShellExecute = true + Verb = "runas"  →  UAC prompt → elevated process
        //   Cannot redirect stdout/stderr with UseShellExecute=true.
        //   Use Tee-Object wrapper to write output to a temp log file we read back.
        //
        // Executable: prefer pwsh.exe (PS 7+), fall back to powershell.exe (5.1).
        // Both accept the same flag set for this use case.
        var exe     = _hostInfo.Executable;
        var tempLog = Path.Combine(Path.GetTempPath(), $"wedm_elv_{Guid.NewGuid():N}.log");
        var wrapper = Path.GetTempFileName() + ".ps1";

        try
        {
            // Wrapper script: invoke the real script, tee output to log file, propagate exit code.
            var scriptQ = scriptPath.Replace("'", "''", StringComparison.Ordinal);
            var logQ    = tempLog.Replace("'", "''", StringComparison.Ordinal);
            var wrapContent = $"""
                $ErrorActionPreference = 'Continue'
                $VerbosePreference = 'SilentlyContinue'
                & '{scriptQ}'{paramArgs} 2>&1 | Tee-Object -FilePath '{logQ}'
                exit $LASTEXITCODE
                """;
            await File.WriteAllTextAsync(wrapper, wrapContent, cancellationToken)
                      .ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName         = exe,
                Arguments        = $"-NoProfile -NonInteractive -NoLogo -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{wrapper}\"",
                UseShellExecute  = true,    // required for runas verb
                Verb             = "runas",
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            };

            _log.Info(
                $"[ProcessLaunch] Elevated PS ({_hostInfo.ExecutableName} Edition={_hostInfo.Edition}) starting.",
                "PowerShell");

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            bool started;
            try { started = process.Start(); }
            catch (Exception ex)
            {
                sw.Stop();
                _log.Error("Elevated PowerShell process failed to start", ex, "PowerShell");
                return PowerShellResult.Fail($"Elevation failed: {ex.Message}", 1, ex);
            }

            if (!started)
            {
                sw.Stop();
                return PowerShellResult.Fail(
                    $"Elevated {_hostInfo.ExecutableName} process did not start — UAC prompt was denied.", 1);
            }

            _log.Info(
                $"[ProcessLaunch] Elevated PS PID={process.Id} ({_hostInfo.ExecutableName}).",
                "PowerShell");

            try
            {
                await process.WaitForExitAsync(waitToken).ConfigureAwait(false);
                process.WaitForExit(); // drain async event callbacks
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                process.WaitForExit(5_000);
                sw.Stop();
                return cancellationToken.IsCancellationRequested
                    ? PowerShellResult.Fail("Execution cancelled.", -1)
                    : PowerShellResult.TimedOutResult(sw.Elapsed);
            }

            sw.Stop();
            var exit = process.ExitCode;

            // Replay captured output through the normal event stream
            if (File.Exists(tempLog))
            {
                var logText = await File.ReadAllTextAsync(tempLog, CancellationToken.None)
                                        .ConfigureAwait(false);
                foreach (var line in SplitLines(logText))
                {
                    outputSb.AppendLine(line);
                    OutputReceived?.Invoke(this, line);
                    _log.ScriptOutput(line);
                }
            }

            var outFinal = outputSb.ToString();
            return new PowerShellResult
            {
                Success     = exit == 0,
                ExitCode    = exit,
                Output      = outFinal,
                OutputLines = SplitLines(outFinal),
                ErrorLines  = [],
                Duration    = sw.Elapsed,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error("Elevated PowerShell process failed", ex, "PowerShell");
            return PowerShellResult.Fail(ex.Message, 1, ex);
        }
        finally
        {
            TryDelete(tempLog);
            TryDelete(wrapper);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static InitialSessionState BuildSafeInitialSessionState(out string strategy)
    {
        // CreateDefault2() registers all built-in cmdlets as SessionStateCmdletEntry objects.
        // No ImportPSModule() calls — those require .psd1 files that don't exist in the SDK.
        // No PSModuleAutoLoadingPreference override — auto-loading is only for $PSModulePath
        // external modules and does not affect built-in cmdlet availability.
        strategy = "CreateDefault2";
        var iss  = InitialSessionState.CreateDefault2();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        return iss;
    }

    private static CancellationTokenSource CreateLinkedTimeoutSource(
        CancellationToken userToken,
        TimeSpan?         operationTimeout)
    {
        if (operationTimeout is { TotalMilliseconds: > 0 })
        {
            if (userToken.CanBeCanceled)
            {
                var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken);
                linked.CancelAfter(operationTimeout.Value);
                return linked;
            }

            var cts = new CancellationTokenSource();
            cts.CancelAfter(operationTimeout.Value);
            return cts;
        }

        return userToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(userToken)
            : new CancellationTokenSource();
    }

    private static List<string> SplitLines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string BuildParamBlock(Dictionary<string, object>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return string.Empty;
        return string.Join(" ", parameters.Select(kv =>
            kv.Value is bool b
                ? (b ? $"-{kv.Key}" : $"-{kv.Key}:$false")
                : $"-{kv.Key} '{kv.Value}'"));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _pool?.Dispose();
        _disposed = true;
    }
}
