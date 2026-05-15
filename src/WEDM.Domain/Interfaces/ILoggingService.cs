using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Structured logging service contract for the WEDM platform.
/// Implementations write to Serilog sinks (file, console, structured JSON, HTML).
/// All logs are correlated by deployment session ID.
/// </summary>
public interface ILoggingService
{
    /// <summary>Current deployment session ID for log correlation.</summary>
    Guid? SessionId { get; }

    void BeginSession(Guid sessionId, string deploymentName);
    void EndSession();

    void Verbose(string message, string? category = null, object? data = null);
    void Debug(string message,   string? category = null, object? data = null);
    void Info(string message,    string? category = null, object? data = null);
    void Warning(string message, string? category = null, object? data = null);
    void Error(string message,   Exception? ex = null, string? category = null);
    void Fatal(string message,   Exception? ex = null, string? category = null);

    /// <summary>Log the start of a named deployment step.</summary>
    void StepStarted(string stepName, int sequence);

    /// <summary>Log successful completion of a step with optional output.</summary>
    void StepSucceeded(string stepName, TimeSpan duration, string? output = null);

    /// <summary>Log failure of a step.</summary>
    void StepFailed(string stepName, string error, int exitCode, Exception? ex = null, string? details = null);

    /// <summary>Log a single line of script output (stdout/stderr).</summary>
    void ScriptOutput(string line, bool isError = false);

    /// <summary>Get all log entries for report generation.</summary>
    IReadOnlyList<LogEntry> GetEntries(LogLevel minimumLevel = LogLevel.Info);

    /// <summary>Write an HTML deployment report to the given path.</summary>
    Task WriteHtmlReportAsync(DeploymentReport report, string outputPath);

    /// <summary>Write a JSON deployment report to the given path.</summary>
    Task WriteJsonReportAsync(DeploymentReport report, string outputPath);

    /// <summary>Raised on each log write for real-time UI binding.</summary>
    event EventHandler<LogEntry>? EntryWritten;
}
