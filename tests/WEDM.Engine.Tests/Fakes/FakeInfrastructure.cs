using System.Collections.Concurrent;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Workflow.Steps;

namespace WEDM.Engine.Tests.Fakes;

// ── FakeLoggingService ────────────────────────────────────────────────────────

/// <summary>
/// In-memory implementation of ILoggingService that captures all log calls.
/// Safe for concurrent use. No-op for HTML/JSON report writing.
/// </summary>
public sealed class FakeLoggingService : ILoggingService
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public Guid? SessionId { get; private set; }

    public event EventHandler<LogEntry>? EntryWritten;

    public void BeginSession(Guid sessionId, string deploymentName)
        => SessionId = sessionId;

    public void EndSession()
        => SessionId = null;

    public void Verbose(string message, string? category = null, object? data = null)
        => Append(LogLevel.Verbose, message, category);

    public void Debug(string message, string? category = null, object? data = null)
        => Append(LogLevel.Debug, message, category);

    public void Info(string message, string? category = null, object? data = null)
        => Append(LogLevel.Info, message, category);

    public void Warning(string message, string? category = null, object? data = null)
        => Append(LogLevel.Warning, message, category);

    public void Error(string message, Exception? ex = null, string? category = null)
        => Append(LogLevel.Error, message, category, ex);

    public void Fatal(string message, Exception? ex = null, string? category = null)
        => Append(LogLevel.Fatal, message, category, ex);

    public void StepStarted(string stepName, int sequence)
        => Append(LogLevel.Info, $"[Step {sequence}] Started: {stepName}", "Step");

    public void StepSucceeded(string stepName, TimeSpan duration, string? output = null)
        => Append(LogLevel.Info, $"Step succeeded: {stepName} ({duration.TotalSeconds:F1}s)", "Step");

    public void StepFailed(string stepName, string error, int exitCode, Exception? ex = null, string? details = null)
        => Append(LogLevel.Error, $"Step failed: {stepName} exit={exitCode} {error}", "Step", ex);

    public void ScriptOutput(string line, bool isError = false)
        => Append(isError ? LogLevel.Error : LogLevel.Verbose, line, "Script");

    public IReadOnlyList<LogEntry> GetEntries(LogLevel minimumLevel = LogLevel.Info)
        => _entries.Where(e => e.Level >= minimumLevel).ToList().AsReadOnly();

    public Task WriteHtmlReportAsync(DeploymentReport report, string outputPath)
        => Task.CompletedTask;

    public Task WriteJsonReportAsync(DeploymentReport report, string outputPath)
        => Task.CompletedTask;

    // Helper for tests
    public IReadOnlyList<LogEntry> AllEntries => _entries.ToList().AsReadOnly();

    private void Append(LogLevel level, string message, string? category, Exception? ex = null)
    {
        var entry = new LogEntry
        {
            Level     = level,
            Message   = message,
            Category  = category ?? string.Empty,
            Exception = ex,
        };
        _entries.Enqueue(entry);
        EntryWritten?.Invoke(this, entry);
    }
}

// ── FakeStepExecutor ──────────────────────────────────────────────────────────

/// <summary>
/// Configurable stub IStepExecutor.  Optionally delays 1 ms to produce measurable Duration.
/// </summary>
public sealed class FakeStepExecutor : IStepExecutor
{
    private readonly bool _succeed;
    private readonly string _output;
    private readonly string _error;
    private readonly bool _delay;

    public FakeStepExecutor(bool succeed = true, string output = "ok", string error = "fail", bool delay = false)
    {
        _succeed = succeed;
        _output  = output;
        _error   = error;
        _delay   = delay;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (_delay)
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);

        return _succeed
            ? StepExecutionResult.Ok(_output, TimeSpan.FromMilliseconds(1))
            : StepExecutionResult.Fail(_error, exitCode: 1);
    }
}

/// <summary>
/// A FakeStepExecutor that throws a given exception (used to verify rollback exception-handling).
/// </summary>
public sealed class ThrowingStepExecutor : IStepExecutor
{
    private readonly Exception _exception;

    public ThrowingStepExecutor(Exception? exception = null)
        => _exception = exception ?? new InvalidOperationException("Simulated executor crash");

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
        => throw _exception;
}

// ── FakeStepExecutorFactory ───────────────────────────────────────────────────

/// <summary>
/// Dictionary-backed fake factory.  Pass step-name → executor and rollback-action → executor
/// mappings through the constructor.  Returns null for anything not in the dictionaries.
/// </summary>
public sealed class FakeStepExecutorFactory : IStepExecutorFactory
{
    private readonly Dictionary<string, IStepExecutor> _executors;
    private readonly Dictionary<string, IStepExecutor> _rollbackExecutors;

    public FakeStepExecutorFactory(
        Dictionary<string, IStepExecutor>? executors         = null,
        Dictionary<string, IStepExecutor>? rollbackExecutors = null)
    {
        _executors         = executors         ?? new Dictionary<string, IStepExecutor>();
        _rollbackExecutors = rollbackExecutors ?? new Dictionary<string, IStepExecutor>();
    }

    public IStepExecutor? GetExecutor(string stepName)
        => _executors.TryGetValue(stepName, out var ex) ? ex : null;

    public IStepExecutor? GetRollbackExecutor(string rollbackAction)
        => _rollbackExecutors.TryGetValue(rollbackAction, out var ex) ? ex : null;
}

// ── FakeOracleHomeBuilder ─────────────────────────────────────────────────────

/// <summary>
/// Creates a temporary Oracle middleware home directory tree on the local file system,
/// suitable for use in integration tests that need real paths.
///
/// Layout created:
///   {root}/MiddlewareHome/wlserver/common/bin/wlst.cmd   (empty sentinel file)
///   {root}/MiddlewareHome/wlserver_10.3/                 (legacy-layout marker directory)
///   {root}/domains/TestDomain/config/config.xml          (minimal WebLogic config)
///
/// Disposing the returned handle deletes the entire temp tree.
/// </summary>
public sealed class FakeOracleHomeBuilder : IDisposable
{
    private readonly string _rootDir;
    private bool _disposed;

    public string MiddlewareHome { get; }
    public string DomainHome     { get; }
    public string WlstCmd        { get; }
    public string ConfigXmlPath  { get; }

    private FakeOracleHomeBuilder()
    {
        _rootDir       = Path.Combine(Path.GetTempPath(), $"WEDM_TestOracle_{Guid.NewGuid():N}");
        MiddlewareHome = Path.Combine(_rootDir, "MiddlewareHome");
        DomainHome     = Path.Combine(_rootDir, "domains", "TestDomain");

        // wlst.cmd
        WlstCmd = Path.Combine(MiddlewareHome, "wlserver", "common", "bin", "wlst.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(WlstCmd)!);
        File.WriteAllText(WlstCmd, string.Empty);

        // wlserver_10.3 layout marker (legacy)
        Directory.CreateDirectory(Path.Combine(MiddlewareHome, "wlserver_10.3"));

        // Minimal WebLogic config.xml
        ConfigXmlPath = Path.Combine(DomainHome, "config", "config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigXmlPath)!);
        File.WriteAllText(ConfigXmlPath, MinimalConfigXml);
    }

    /// <summary>Creates the fake Oracle home and returns an instance that cleans up on Dispose().</summary>
    public static FakeOracleHomeBuilder Create() => new();

    private const string MinimalConfigXml = """
        <?xml version='1.0' encoding='UTF-8'?>
        <domain xmlns='http://xmlns.oracle.com/weblogic/domain'>
          <name>TestDomain</name>
          <server>
            <name>AdminServer</name>
            <listen-port>7001</listen-port>
          </server>
          <server>
            <name>ms1</name>
            <listen-port>8001</listen-port>
          </server>
        </domain>
        """;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_rootDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
