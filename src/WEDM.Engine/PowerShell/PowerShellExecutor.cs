using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using WEDM.Domain.Interfaces;

namespace WEDM.Engine.PowerShell;

/// <summary>
/// Production PowerShell executor using the PowerShell SDK for in-process execution
/// and System.Diagnostics.Process for out-of-process elevated execution.
///
/// Design decisions:
///   • In-process runspace pool for fast, non-elevated script execution
///   • Out-of-process powershell.exe for Administrator-elevated operations
///   • Optional wall-clock <see cref="TimeSpan"/> caps (OUI installs, RCU, WLST)
///   • Streaming stdout/stderr output via events for real-time log viewer binding
///   • Full structured result with exit code, duration, timeout flag, and exceptions
/// </summary>
public sealed class PowerShellExecutor : IPowerShellExecutor, IDisposable
{
    private readonly ILoggingService _log;
    private readonly RunspacePool   _pool;
    private bool _disposed;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ErrorReceived;

    public PowerShellExecutor(ILoggingService log)
    {
        _log = log;

        // CreateDefault2 builds a minimal session that loads only the Core module
        // (Microsoft.PowerShell.Core) eagerly and defers all other modules via
        // module auto-loading.  That lazy auto-loading is the source of the startup
        // message "The 'Set-Location' command was found in the module
        // 'Microsoft.PowerShell.Management'…" which can hang or produce unexpected
        // output in a WPF host with no interactive console.
        //
        // Fix: disable module auto-loading (PSModuleAutoLoadingPreference = None) and
        // pre-import only the management module we actually need.  All subsequent
        // commands are fully satisfied without any on-demand disk probing.
        var iss = InitialSessionState.CreateDefault2();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        // Disable module auto-loading for non-interactive / headless execution.
        // Scripts that need additional modules must import them explicitly.
        iss.Variables.Add(new SessionStateVariableEntry(
            "PSModuleAutoLoadingPreference",
            "None",
            "Disable auto-loading to prevent startup hangs in WPF host",
            ScopedItemOptions.AllScope));

        // Pre-import Microsoft.PowerShell.Management so Set-Location, Get-ChildItem,
        // Start-Process etc. are immediately available without a lazy module load.
        iss.ImportPSModule("Microsoft.PowerShell.Management");
        iss.ImportPSModule("Microsoft.PowerShell.Utility");

        // WPF has no PSHost. Use the factory overload that binds only InitialSessionState
        // (pool min/max are both 1 — sufficient for this host tool).
        _pool = RunspaceFactory.CreateRunspacePool(iss);
        try
        {
            _pool.Open();
            _log.Info(
                "PowerShell in-process runspace pool ready (InitialSessionState, Management+Utility pre-loaded).",
                "PowerShell");
        }
        catch (Exception ex)
        {
            _log.Error("PowerShell runspace pool initialization failed.", ex, "PowerShell");
            try { _pool.Dispose(); } catch { /* ignore */ }
            throw;
        }
    }

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
            return await ExecuteElevatedAsync(scriptPath, parameters, workingDirectory, cancellationToken, operationTimeout);

        var escaped = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        return await ExecuteInProcessAsync(
            $"& '{escaped}'", parameters, workingDirectory, cancellationToken, operationTimeout);
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
            var cwd = workingDirectory ?? Directory.GetCurrentDirectory();
            var cwdEsc = cwd.Replace("'", "''", StringComparison.Ordinal);
            var tmpScript = Path.Combine(Path.GetTempPath(), $"wedm_{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(tmpScript,
                $"Set-Location -LiteralPath '{cwdEsc}'\n{command}",
                cancellationToken);
            try { return await ExecuteElevatedAsync(tmpScript, null, workingDirectory, cancellationToken, operationTimeout); }
            finally { TryDelete(tmpScript); }
        }

        return await ExecuteInProcessAsync(command, null, workingDirectory, cancellationToken, operationTimeout);
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
        var modEsc = modulePath.Replace("'", "''", StringComparison.Ordinal);
        var command = $"Import-Module -Force -LiteralPath '{modEsc}' -ErrorAction Stop; {functionName} {paramBlock}";
        return await ExecuteInProcessAsync(command, null, null, cancellationToken, operationTimeout);
    }

    private async Task<PowerShellResult> ExecuteInProcessAsync(
        string command,
        Dictionary<string, object>? parameters,
        string? workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout)
    {
        var sw = Stopwatch.StartNew();
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
                using var linked = CreateLinkedTimeoutSource(cancellationToken, operationTimeout);
                var gate = Task.Delay(Timeout.Infinite, linked.Token);
                var completed = await Task.WhenAny(invokeTask, gate).ConfigureAwait(false);
                if (completed != invokeTask)
                {
                    try { ps.Stop(); } catch { /* ignore */ }
                    sw.Stop();
                    if (cancellationToken.IsCancellationRequested)
                        return PowerShellResult.Fail("Execution cancelled.", -1);
                    return PowerShellResult.TimedOutResult(sw.Elapsed);
                }

                await invokeTask.ConfigureAwait(false);
            }

            sw.Stop();

            // ── Extract __WEDM_EXIT marker (written by BuildWlstLaunchBody) ──────────
            // This is the authoritative exit code for all WLST / Start-Process launch
            // scripts, bypassing ps.HadErrors which fires on any native-command stderr
            // output (including WLST informational messages) regardless of actual success.
            int? wedmExplicitExit = null;
            var cleanOutputLines  = outputLines.ToList();
            for (int i = cleanOutputLines.Count - 1; i >= 0; i--)
            {
                var marker = cleanOutputLines[i].Trim();
                if (marker.StartsWith("__WEDM_EXIT:", StringComparison.Ordinal)
                    && int.TryParse(marker["__WEDM_EXIT:".Length..], out var parsedRc))
                {
                    wedmExplicitExit = parsedRc;
                    cleanOutputLines.RemoveAt(i);   // strip internal marker from user-visible output
                    break;
                }
            }

            bool hadErrors;
            int  exitCode;
            if (wedmExplicitExit.HasValue)
            {
                // WLST / Start-Process script with explicit exit code — trust it entirely.
                exitCode  = wedmExplicitExit.Value;
                hadErrors = exitCode != 0;
            }
            else
            {
                // Cmdlet-only script (no Start-Process wrapper).
                // Only fail when ErrorRecord objects carry an actual exception; bare informational
                // error-stream messages (e.g., Set-Location on non-existent path warning) should
                // not abort a step that otherwise completes successfully.
                hadErrors = ps.HadErrors && ps.Streams.Error.Any(e => e?.Exception is not null);
                exitCode  = hadErrors ? 1 : 0;
            }

            var output = string.Join(Environment.NewLine, cleanOutputLines);
            var errors = string.Join(Environment.NewLine, errorLines);

            return new PowerShellResult
            {
                Success     = !hadErrors,
                ExitCode    = exitCode,
                Output      = output,
                Errors      = errors,
                OutputLines = cleanOutputLines,
                ErrorLines  = errorLines,
                Duration    = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            try { ps.Stop(); } catch { /* ignore */ }
            sw.Stop();
            if (cancellationToken.IsCancellationRequested)
                return PowerShellResult.Fail("Execution cancelled.", -1);
            return PowerShellResult.TimedOutResult(sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error("PowerShell execution failed", ex, "PowerShell");
            return PowerShellResult.Fail(ex.Message, 1, ex);
        }
    }

    private async Task<PowerShellResult> ExecuteElevatedAsync(
        string scriptPath,
        Dictionary<string, object>? parameters,
        string? workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout)
    {
        var sw       = Stopwatch.StartNew();
        var outputSb = new StringBuilder();
        var errorSb  = new StringBuilder();

        var paramArgs = string.Empty;
        if (parameters?.Count > 0)
            paramArgs = " " + string.Join(" ",
                parameters.Select(kv => $"-{kv.Key} \"{kv.Value}\""));

        using CancellationTokenSource? linkedCts =
            (operationTimeout is { TotalMilliseconds: > 0 } || cancellationToken.CanBeCanceled)
                ? CreateLinkedTimeoutSource(cancellationToken, operationTimeout)
                : null;
        var waitToken = linkedCts?.Token ?? CancellationToken.None;

        // ── Elevation via ShellExecute runas ────────────────────────────────
        // UseShellExecute must be TRUE for the runas verb.
        // This means we cannot redirect stdout/stderr through managed pipes.
        // Instead we have the script write its output to a temp log file that
        // we read back after the elevated process exits.
        //
        // Hardening flags:
        //   -NoProfile        — skips $PROFILE loading (no interactive side-effects)
        //   -NonInteractive   — disables prompts (read-host, confirmations)
        //   -NoLogo           — suppresses the banner header
        //   -ExecutionPolicy Bypass — overrides any machine policy for this call
        //   -WindowStyle Hidden — hides the console window from the desktop

        var tempLog   = Path.Combine(Path.GetTempPath(), $"wedm_elv_{Guid.NewGuid():N}.log");
        var logScriptPath = Path.GetTempFileName() + ".ps1";

        try
        {
            // Wrap the original script in a tee-to-file wrapper so we capture output.
            // "exit $LASTEXITCODE" propagates the child script's exit code through the wrapper.
            var scriptQ = scriptPath.Replace("'", "''", StringComparison.Ordinal);
            var logQ    = tempLog.Replace("'", "''", StringComparison.Ordinal);
            var wrapper = $"""
                $ErrorActionPreference = 'Continue'
                $VerbosePreference = 'SilentlyContinue'
                & '{scriptQ}'{paramArgs} 2>&1 | Tee-Object -FilePath '{logQ}'
                exit $LASTEXITCODE
                """;
            await File.WriteAllTextAsync(logScriptPath, wrapper, cancellationToken).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -NonInteractive -NoLogo -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{logScriptPath}\"",
                UseShellExecute = true,   // required for runas elevation
                Verb            = "runas",
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            };

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
                return PowerShellResult.Fail("Elevated powershell.exe process did not start — UAC denied.", 1);
            }

            _log.Info($"[ProcessLaunch] Elevated PowerShell PID={process.Id}", "PowerShell");

            try
            {
                await process.WaitForExitAsync(waitToken).ConfigureAwait(false);
                process.WaitForExit(); // ensure all I/O is flushed
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

            // Read captured log file
            string outText = string.Empty;
            if (File.Exists(tempLog))
            {
                outText = await File.ReadAllTextAsync(tempLog, CancellationToken.None)
                                    .ConfigureAwait(false);
                foreach (var line in SplitLines(outText))
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
                Errors      = errorSb.ToString(),
                OutputLines = SplitLines(outFinal),
                ErrorLines  = SplitLines(errorSb.ToString()),
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
            TryDelete(logScriptPath);
        }
    }

    private static CancellationTokenSource CreateLinkedTimeoutSource(
        CancellationToken userToken,
        TimeSpan? operationTimeout)
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
        => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();

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
        _pool.Dispose();
        _disposed = true;
    }
}
