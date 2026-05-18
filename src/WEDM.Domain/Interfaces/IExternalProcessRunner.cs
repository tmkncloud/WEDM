using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Contract for launching external processes (OUI, WLST, OPatch, RCU, JDK installers) in a
/// deadlock-safe, observable, cancellable manner.
///
/// Design guarantee: stdout and stderr are always drained asynchronously on background threads,
/// regardless of how much output the child emits.  No synchronous ReadToEnd() or WaitForExit()
/// before pipe draining is ever used.
/// </summary>
public interface IExternalProcessRunner
{
    /// <summary>
    /// Run an external process and wait for it to exit, streaming output in real time.
    /// </summary>
    /// <param name="options">All process launch parameters, timeouts, and watchdog settings.</param>
    /// <param name="onStdout">Optional per-line stdout callback fired on a thread-pool thread.</param>
    /// <param name="onStderr">Optional per-line stderr callback fired on a thread-pool thread.</param>
    /// <param name="cancellationToken">Cooperative cancellation; kills the process tree on request.</param>
    /// <returns>Structured result including exit code, output, timing, and optional diagnostics.</returns>
    Task<ExternalProcessResult> RunAsync(
        ExternalProcessOptions options,
        Action<string>?        onStdout           = null,
        Action<string>?        onStderr           = null,
        CancellationToken      cancellationToken  = default);

    /// <summary>Fired for each stdout line received from the child process.</summary>
    event EventHandler<string>? OutputReceived;

    /// <summary>Fired for each stderr line received from the child process.</summary>
    event EventHandler<string>? ErrorReceived;
}
