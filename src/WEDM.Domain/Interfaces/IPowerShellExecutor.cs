namespace WEDM.Domain.Interfaces;

/// <summary>
/// Contract for executing PowerShell scripts and inline commands from C# code.
/// Decouples the automation engine from the actual PowerShell runtime,
/// enabling easy testing and alternative executor implementations.
/// </summary>
public interface IPowerShellExecutor
{
    /// <summary>
    /// Execute a PowerShell script file with optional named parameters.
    /// Returns the structured execution result including stdout, stderr, exit code and duration.
    /// </summary>
    /// <param name="scriptPath">Path to a .ps1 file.</param>
    /// <param name="parameters">Optional named parameters passed into the runspace.</param>
    /// <param name="workingDirectory">Initial working directory.</param>
    /// <param name="runAsAdministrator">When true, runs via elevated helper process.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <param name="operationTimeout">Optional wall-clock timeout.</param>
    Task<PowerShellResult> ExecuteScriptAsync(
        string scriptPath,
        Dictionary<string, object>? parameters = null,
        string? workingDirectory = null,
        bool runAsAdministrator = false,
        CancellationToken cancellationToken = default,
        TimeSpan? operationTimeout = null);

    /// <summary>
    /// Execute an inline PowerShell command string.
    /// Useful for short imperative operations (registry writes, dir creation, service install).
    /// </summary>
    /// <param name="command">PowerShell script block as a string.</param>
    /// <param name="workingDirectory">Initial location; null uses current directory.</param>
    /// <param name="runAsAdministrator">When true, runs via elevated helper process.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <param name="operationTimeout">Hard cap on wall-clock runtime (independent of <paramref name="cancellationToken"/>).</param>
    Task<PowerShellResult> ExecuteCommandAsync(
        string command,
        string? workingDirectory = null,
        bool runAsAdministrator = false,
        CancellationToken cancellationToken = default,
        TimeSpan? operationTimeout = null);

    /// <summary>
    /// Execute a PowerShell module function by loading a .psm1 and calling a named function.
    /// </summary>
    /// <param name="modulePath">Path to the .psm1 module file.</param>
    /// <param name="functionName">Function to invoke after import.</param>
    /// <param name="parameters">Optional named parameters.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <param name="operationTimeout">Optional wall-clock timeout.</param>
    Task<PowerShellResult> ExecuteModuleFunctionAsync(
        string modulePath,
        string functionName,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default,
        TimeSpan? operationTimeout = null);

    /// <summary>
    /// Stream output: fires OutputReceived for each stdout line during execution.
    /// </summary>
    event EventHandler<string>? OutputReceived;

    /// <summary>
    /// Fires for each stderr line.
    /// </summary>
    event EventHandler<string>? ErrorReceived;
}

/// <summary>
/// Result returned by any PowerShell execution call.
/// </summary>
public sealed class PowerShellResult
{
    public bool     Success    { get; init; }
    public int      ExitCode   { get; init; }
    public string   Output     { get; init; } = string.Empty;
    public string   Errors     { get; init; } = string.Empty;
    public bool     TimedOut   { get; init; }
    public TimeSpan Duration   { get; init; }
    public IReadOnlyList<string> OutputLines { get; init; } = [];
    public IReadOnlyList<string> ErrorLines  { get; init; } = [];
    public Exception? Exception { get; init; }

    public static PowerShellResult Ok(string output, TimeSpan dur)
        => new() { Success = true, ExitCode = 0, Output = output, Duration = dur };

    public static PowerShellResult Fail(string error, int code = 1, Exception? ex = null)
        => new() { Success = false, ExitCode = code, Errors = error, Exception = ex };

    public static PowerShellResult TimedOutResult(TimeSpan waited)
        => new()
        {
            Success  = false,
            ExitCode = -2,
            TimedOut = true,
            Errors   = $"Operation timed out after {waited}.",
            Duration = waited
        };
}
