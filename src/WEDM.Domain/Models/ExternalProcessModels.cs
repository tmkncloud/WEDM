namespace WEDM.Domain.Models;

// ── Timeout presets ───────────────────────────────────────────────────────────

/// <summary>
/// Well-known wall-clock timeout presets for every class of external tool
/// that WEDM invokes. Shared between ExternalProcessRunner and PowerShellExecutor.
/// </summary>
public static class ExternalProcessTimeouts
{
    /// <summary>Total cap for OUI / WLST domain creation / RCU (90 min).</summary>
    public static readonly TimeSpan DomainCreation = TimeSpan.FromMinutes(90);

    /// <summary>Total cap for OPatch application (30 min).</summary>
    public static readonly TimeSpan PatchApplication = TimeSpan.FromMinutes(30);

    /// <summary>Total cap for JDK silent installs (15 min).</summary>
    public static readonly TimeSpan JdkInstall = TimeSpan.FromMinutes(15);

    /// <summary>Total cap for Windows service registration scripts (15 min).</summary>
    public static readonly TimeSpan ServiceRegistration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Watchdog fires if no stdout/stderr activity is seen for this duration.
    /// A process that makes no output for 5 continuous minutes is considered hung.
    /// </summary>
    public static readonly TimeSpan SilentProcess = TimeSpan.FromMinutes(5);

    /// <summary>
    /// If an expected grandchild process (e.g. java.exe) has not appeared within this
    /// window after our direct child started, the launch is classified as failed.
    /// </summary>
    public static readonly TimeSpan LaunchVerification = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time allowed for an elevated process spawn (UAC + elevation). </summary>
    public static readonly TimeSpan ElevatedLaunch = TimeSpan.FromSeconds(120);
}

// ── Options ───────────────────────────────────────────────────────────────────

/// <summary>
/// Options for a single <see cref="IExternalProcessRunner"/> invocation.
/// All timeouts default to conservative production values and can be overridden
/// for shorter-running operations or tests.
/// </summary>
public sealed class ExternalProcessOptions
{
    /// <summary>Executable to launch.  Full path or PATH-resolvable name.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Command-line arguments string.  The runner does NOT shell-expand these.</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>Working directory; <c>null</c> = inherit the caller's CWD.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment variables merged into the child process environment.
    /// Existing variables with the same name (case-insensitive on Windows) are overridden.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>Total wall-clock cap; default <see cref="ExternalProcessTimeouts.DomainCreation"/>.</summary>
    public TimeSpan TotalTimeout { get; init; } = ExternalProcessTimeouts.DomainCreation;

    /// <summary>
    /// Watchdog fires if stdout+stderr have been silent for this duration.
    /// Default <see cref="ExternalProcessTimeouts.SilentProcess"/>.
    /// Set to <see cref="Timeout.InfiniteTimeSpan"/> to disable the activity check.
    /// </summary>
    public TimeSpan SilentProcessTimeout { get; init; } = ExternalProcessTimeouts.SilentProcess;

    /// <summary>
    /// When <see cref="ExpectedChildProcessName"/> is set, the runner polls the process
    /// table until a matching grandchild appears or this timeout elapses.
    /// </summary>
    public TimeSpan LaunchVerificationTimeout { get; init; } = ExternalProcessTimeouts.LaunchVerification;

    /// <summary>
    /// Optional process name (without extension) that must appear as a descendant of the
    /// launched process within <see cref="LaunchVerificationTimeout"/>.
    /// Example: <c>"java"</c> — ensures wlst.cmd actually spawned the JVM.
    /// </summary>
    public string? ExpectedChildProcessName { get; init; }

    /// <summary>
    /// Human-readable label used in <c>[ProcessLaunch]</c> telemetry log lines.
    /// </summary>
    public string Label { get; init; } = "ExternalProcess";

    /// <summary>
    /// How many trailing stdout/stderr lines to capture in crash diagnostics.
    /// Default 50.
    /// </summary>
    public int DiagnosticsTailLines { get; init; } = 50;

    /// <summary>
    /// When <c>true</c> (default) a background watchdog monitors process activity
    /// and terminates the process tree if <see cref="SilentProcessTimeout"/> elapses
    /// with no output.
    /// </summary>
    public bool EnableWatchdog { get; init; } = true;
}

// ── Result ────────────────────────────────────────────────────────────────────

/// <summary>Result returned by <see cref="IExternalProcessRunner.RunAsync"/>.</summary>
public sealed class ExternalProcessResult
{
    /// <summary>
    /// <c>true</c> only when the process exited with code 0 and no abort condition
    /// (timeout / hang / launch failure / cancellation) occurred.
    /// </summary>
    public bool Success => ExitCode == 0 && !TimedOut && !Hung && !LaunchFailed && !Cancelled;

    public int     ExitCode     { get; init; }

    /// <summary>Total timeout (<see cref="ExternalProcessOptions.TotalTimeout"/>) elapsed.</summary>
    public bool    TimedOut     { get; init; }

    /// <summary>Watchdog detected a silent process and terminated it.</summary>
    public bool    Hung         { get; init; }

    /// <summary>The process did not start, or the expected child never appeared.</summary>
    public bool    LaunchFailed { get; init; }

    public bool    Cancelled    { get; init; }

    /// <summary>Human-readable reason when <see cref="LaunchFailed"/> is <c>true</c>.</summary>
    public string  LaunchFailureReason { get; init; } = string.Empty;

    public string  Output     { get; init; } = string.Empty;
    public string  Errors     { get; init; } = string.Empty;

    public IReadOnlyList<string> OutputLines { get; init; } = [];
    public IReadOnlyList<string> ErrorLines  { get; init; } = [];

    public TimeSpan Duration  { get; init; }

    /// <summary>OS PID of the direct child process, or 0 if the process never started.</summary>
    public int      Pid       { get; init; }

    /// <summary>Rich diagnostics; populated for Hung / Timeout / LaunchFailed results.</summary>
    public ExternalProcessCrashDiagnostics? Diagnostics { get; init; }

    // ── Static factories ─────────────────────────────────────────────────────

    public static ExternalProcessResult FromCancelled(TimeSpan elapsed)
        => new() { ExitCode = -1, Cancelled = true, Duration = elapsed };

    public static ExternalProcessResult FromTimeout(
        TimeSpan elapsed,
        IReadOnlyList<string> outLines,
        IReadOnlyList<string> errLines)
        => new()
        {
            ExitCode    = -2,
            TimedOut    = true,
            Duration    = elapsed,
            Output      = string.Join(Environment.NewLine, outLines),
            Errors      = string.Join(Environment.NewLine, errLines),
            OutputLines = outLines,
            ErrorLines  = errLines,
        };

    public static ExternalProcessResult FromHung(
        TimeSpan elapsed,
        IReadOnlyList<string> outLines,
        IReadOnlyList<string> errLines,
        ExternalProcessCrashDiagnostics? diagnostics = null)
        => new()
        {
            ExitCode    = -3,
            Hung        = true,
            Duration    = elapsed,
            Output      = string.Join(Environment.NewLine, outLines),
            Errors      = string.Join(Environment.NewLine, errLines),
            OutputLines = outLines,
            ErrorLines  = errLines,
            Diagnostics = diagnostics,
        };

    public static ExternalProcessResult FromLaunchFailure(
        string reason,
        TimeSpan elapsed,
        IReadOnlyList<string>? outLines = null,
        IReadOnlyList<string>? errLines = null,
        ExternalProcessCrashDiagnostics? diagnostics = null)
        => new()
        {
            ExitCode            = -4,
            LaunchFailed        = true,
            LaunchFailureReason = reason,
            Duration            = elapsed,
            OutputLines         = outLines ?? [],
            ErrorLines          = errLines ?? [],
            Output              = outLines is null ? string.Empty : string.Join(Environment.NewLine, outLines),
            Errors              = errLines is null ? string.Empty : string.Join(Environment.NewLine, errLines),
            Diagnostics         = diagnostics,
        };
}

// ── Crash diagnostics ─────────────────────────────────────────────────────────

/// <summary>
/// Rich diagnostics snapshot captured when a process hangs, times out, or fails to start.
/// Included in the deployment report and emitted to the structured log.
/// </summary>
public sealed class ExternalProcessCrashDiagnostics
{
    public string CommandLine      { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>Snapshot of the environment variables passed to the process.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentSnapshot { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Last N stdout lines at the time of abort.</summary>
    public string StdoutTail { get; init; } = string.Empty;

    /// <summary>Last N stderr lines at the time of abort.</summary>
    public string StderrTail { get; init; } = string.Empty;

    /// <summary>Process tree snapshot (indented text lines).</summary>
    public IReadOnlyList<string> ChildProcessTree { get; init; } = [];

    public string WaitReason          { get; init; } = string.Empty;
    public string HangClassification  { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt  { get; init; } = DateTimeOffset.UtcNow;
}
