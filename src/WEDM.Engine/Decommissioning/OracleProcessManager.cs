using System.Diagnostics;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Decommissioning;

public sealed class OracleProcessManager : IOracleProcessManager
{
    private static readonly string[] TargetProcessNames =
    [
        "java", "javaw", "NodeManager", "opatch", "oui", "wlst", "wlsvc"
    ];

    private static readonly string[] MiddlewareKeywords =
    [
        "weblogic", "oracle", "nodemanager", "forms", "reports", "ohs", "opatch", "fmw", "middleware"
    ];

    public IReadOnlyList<OracleProcessDescriptor> DetectMiddlewareProcesses()
    {
        var results = new List<OracleProcessDescriptor>();

        foreach (var name in TargetProcessNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    var cmd = TryGetCommandLine(proc);
                    if (!IsMiddlewareProcess(name, cmd))
                    {
                        proc.Dispose();
                        continue;
                    }

                    results.Add(new OracleProcessDescriptor
                    {
                        ProcessId   = proc.Id,
                        ProcessName = proc.ProcessName,
                        CommandLine = cmd,
                        Category    = Classify(name, cmd),
                    });
                }
                catch
                {
                    // access denied for some system processes
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        return results;
    }

    public async Task<ProcessStopResult> StopProcessesAsync(
        IEnumerable<OracleProcessDescriptor> processes,
        bool forceAfterTimeout,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var stopped  = 0;
        var failed   = 0;

        foreach (var descriptor in processes.DistinctBy(p => p.ProcessId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var proc = Process.GetProcessById(descriptor.ProcessId);
                if (proc.HasExited)
                {
                    stopped++;
                    continue;
                }

                proc.CloseMainWindow();
                var exited = await WaitForExitAsync(proc, gracefulTimeout, cancellationToken).ConfigureAwait(false);

                if (!exited && forceAfterTimeout)
                {
                    proc.Kill(entireProcessTree: true);
                    exited = await WaitForExitAsync(proc, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                }

                if (exited)
                {
                    stopped++;
                    messages.Add($"Stopped {descriptor.ProcessName} (PID {descriptor.ProcessId}).");
                }
                else
                {
                    failed++;
                    messages.Add($"Failed to stop {descriptor.ProcessName} (PID {descriptor.ProcessId}) within timeout.");
                }
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"Error stopping PID {descriptor.ProcessId}: {ex.Message}");
            }
        }

        return new ProcessStopResult
        {
            StoppedCount = stopped,
            FailedCount  = failed,
            Messages     = messages,
        };
    }

    private static bool IsMiddlewareProcess(string processName, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return processName.Equals("java", StringComparison.OrdinalIgnoreCase)
                || processName.Equals("javaw", StringComparison.OrdinalIgnoreCase);

        var lower = commandLine.ToLowerInvariant();
        return MiddlewareKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    private static string Classify(string processName, string? commandLine)
    {
        var text = $"{processName} {commandLine}".ToLowerInvariant();
        if (text.Contains("nodemanager")) return "NodeManager";
        if (text.Contains("adminserver") || text.Contains("weblogic.server")) return "AdminServer";
        if (text.Contains("managed")) return "ManagedServer";
        if (text.Contains("opatch")) return "OPatch";
        if (text.Contains("forms")) return "Forms";
        if (text.Contains("reports")) return "Reports";
        if (text.Contains("ohs") || text.Contains("ohs_module")) return "OHS";
        if (text.Contains("oui")) return "OUI";
        if (text.Contains("wlst")) return "WLST";
        return "Middleware";
    }

    private static string? TryGetCommandLine(Process proc)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {proc.Id}");
            foreach (var obj in searcher.Get())
                return obj["CommandLine"]?.ToString();
        }
        catch
        {
            return proc.MainModule?.FileName;
        }

        return null;
    }

    private static async Task<bool> WaitForExitAsync(
        Process proc,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return proc.HasExited;
        }
        catch (OperationCanceledException)
        {
            return proc.HasExited;
        }
    }
}
