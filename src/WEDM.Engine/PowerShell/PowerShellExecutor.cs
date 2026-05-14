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
        var iss = InitialSessionState.CreateDefault2();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        // WPF has no PSHost. Overload (min, max, iss, host) requires a non-null host. Use the factory overload
        // that binds only InitialSessionState (pool min/max are both 1 — sufficient for this host tool).
        _pool = RunspaceFactory.CreateRunspacePool(iss);
        try
        {
            _pool.Open();
            _log.Info(
                "PowerShell in-process runspace pool ready (InitialSessionState, single runspace capacity).",
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
            var hadErrors = ps.HadErrors || errorLines.Count > 0;
            var output = string.Join(Environment.NewLine, outputLines);
            var errors = string.Join(Environment.NewLine, errorLines);

            return new PowerShellResult
            {
                Success     = !hadErrors,
                ExitCode    = hadErrors ? 1 : 0,
                Output      = output,
                Errors      = errors,
                OutputLines = outputLines,
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
        var sw = Stopwatch.StartNew();
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

        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-NonInteractive -ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\"{paramArgs}",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDirectory ?? Directory.GetCurrentDirectory(),
            Verb                   = "runas"
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            outputSb.AppendLine(e.Data);
            OutputReceived?.Invoke(this, e.Data);
            _log.ScriptOutput(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            errorSb.AppendLine(e.Data);
            ErrorReceived?.Invoke(this, e.Data);
            _log.ScriptOutput(e.Data, isError: true);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(waitToken).ConfigureAwait(false);

            sw.Stop();
            var exit = process.ExitCode;
            var outText = outputSb.ToString();
            var errText = errorSb.ToString();

            return new PowerShellResult
            {
                Success     = exit == 0,
                ExitCode    = exit,
                Output      = outText,
                Errors      = errText,
                OutputLines = SplitLines(outText),
                ErrorLines  = SplitLines(errText),
                Duration    = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            sw.Stop();
            if (cancellationToken.IsCancellationRequested)
                return PowerShellResult.Fail("Execution cancelled.", -1);
            return PowerShellResult.TimedOutResult(sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error("Elevated PowerShell process failed", ex, "PowerShell");
            return PowerShellResult.Fail(ex.Message, 1, ex);
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
