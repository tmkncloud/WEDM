using System.Collections.Concurrent;
using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Security;

namespace WEDM.Infrastructure.Logging;

/// <summary>
/// Production Serilog-backed logging service.
/// Writes to:
///   • Rotating daily file logs (structured JSON via Compact formatter)
///   • Console sink with colored output
///   • In-memory buffer for real-time UI binding and HTML report generation
/// All log entries are correlated by deployment session ID.
/// Thread-safe for concurrent step execution.
/// </summary>
public sealed class SerilogLoggingService : ILoggingService, IDisposable
{
    private readonly string _logDirectory;
    private Logger? _logger;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private Guid? _sessionId;
    private string _deploymentName = "Unknown";

    public event EventHandler<LogEntry>? EntryWritten;
    public Guid? SessionId => _sessionId;

    public SerilogLoggingService(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(logDirectory);
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────

    public void BeginSession(Guid sessionId, string deploymentName)
    {
        _sessionId     = sessionId;
        _deploymentName = deploymentName;
        _entries.Clear();

        var logFile = Path.Combine(_logDirectory, $"wedm-{sessionId:N}.log");
        var jsonFile = Path.Combine(_logDirectory, $"wedm-{sessionId:N}.json");

        _logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.WithProperty("SessionId", sessionId)
            .Enrich.WithProperty("DeploymentName", deploymentName)
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .Enrich.WithThreadId()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] [{Category}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logFile,
                rollingInterval: RollingInterval.Infinite,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [{Category}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(new Serilog.Formatting.Compact.CompactJsonFormatter(), jsonFile,
                rollingInterval: RollingInterval.Infinite)
            .CreateLogger();

        Info($"=== WEDM Session Started: {deploymentName} [{sessionId}] ===", "Session");
    }

    public void EndSession()
    {
        Info($"=== WEDM Session Ended: {_deploymentName} ===", "Session");
        _logger?.Dispose();
        _logger = null;
    }

    // ── Core log methods ──────────────────────────────────────────────────────

    public void Verbose(string message, string? category = null, object? data = null)
        => Write(LogLevel.Verbose, message, category);

    public void Debug(string message, string? category = null, object? data = null)
        => Write(LogLevel.Debug, message, category);

    public void Info(string message, string? category = null, object? data = null)
        => Write(LogLevel.Info, message, category);

    public void Warning(string message, string? category = null, object? data = null)
        => Write(LogLevel.Warning, message, category);

    public void Error(string message, Exception? ex = null, string? category = null)
        => Write(LogLevel.Error, message, category, ex);

    public void Fatal(string message, Exception? ex = null, string? category = null)
        => Write(LogLevel.Fatal, message, category, ex);

    // ── Step lifecycle ────────────────────────────────────────────────────────

    public void StepStarted(string stepName, int sequence)
    {
        var msg = $"▶  Step [{sequence:D2}] STARTED: {stepName}";
        Write(LogLevel.Info, msg, "Workflow");
    }

    public void StepSucceeded(string stepName, TimeSpan duration, string? output = null)
    {
        output = string.IsNullOrEmpty(output) ? null : SecretRedactor.Redact(output);
        var msg = $"✔  Step SUCCEEDED: {stepName}  [{duration.TotalSeconds:F1}s]";
        Write(LogLevel.Info, msg, "Workflow", details: output);
    }

    public void StepFailed(string stepName, string error, int exitCode, Exception? ex = null)
    {
        var msg = $"✘  Step FAILED: {stepName}  [exit={exitCode}]  {error}";
        Write(LogLevel.Error, msg, "Workflow", ex);
    }

    public void ScriptOutput(string line, bool isError = false)
    {
        var level = isError ? LogLevel.Warning : LogLevel.Verbose;
        Write(level, line, "Script");
    }

    // ── Querying ──────────────────────────────────────────────────────────────

    public IReadOnlyList<LogEntry> GetEntries(LogLevel minimumLevel = LogLevel.Info)
        => _entries.Where(e => e.Level >= minimumLevel).ToList();

    // ── Report generation ─────────────────────────────────────────────────────

    public async Task WriteHtmlReportAsync(DeploymentReport report, string outputPath)
    {
        var html = GenerateHtmlReport(report);
        await File.WriteAllTextAsync(outputPath, html, Encoding.UTF8);
        Info($"HTML report written → {outputPath}", "Reporting");
    }

    public async Task WriteJsonReportAsync(DeploymentReport report, string outputPath)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json, Encoding.UTF8);
        Info($"JSON report written → {outputPath}", "Reporting");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Write(LogLevel level, string message, string? category = null,
        Exception? ex = null, string? details = null, string? stepName = null)
    {
        category ??= "General";
        message = SecretRedactor.Redact(message);
        details = string.IsNullOrEmpty(details) ? null : SecretRedactor.Redact(details);

        var entry = new LogEntry
        {
            Level     = level,
            Category  = category,
            Message   = message,
            Exception = ex,
            Details   = details,
            StepName  = stepName
        };

        _entries.Enqueue(entry);
        EntryWritten?.Invoke(this, entry);

        if (_logger == null) return;

        var serilogLevel = level switch
        {
            LogLevel.Verbose => LogEventLevel.Verbose,
            LogLevel.Debug   => LogEventLevel.Debug,
            LogLevel.Info    => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error   => LogEventLevel.Error,
            LogLevel.Fatal   => LogEventLevel.Fatal,
            _                => LogEventLevel.Information
        };

        _logger.ForContext("Category", category).Write(serilogLevel, ex, "{Message}", message);
    }

    private static string GenerateHtmlReport(DeploymentReport report)
    {
        var sb = new StringBuilder();
        var statusColor = report.FinalStatus switch
        {
            Domain.Enums.DeploymentStatus.Completed   => "#3FB950",
            Domain.Enums.DeploymentStatus.Failed      => "#F85149",
            Domain.Enums.DeploymentStatus.PartialFail => "#D29922",
            Domain.Enums.DeploymentStatus.RolledBack  => "#A371F7",
            _                                          => "#8B949E"
        };

        sb.AppendLine("<!DOCTYPE html><html lang='en'><head>");
        sb.AppendLine("<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>");
        sb.AppendLine($"<title>WEDM Deployment Report — {report.DeploymentName}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("*{box-sizing:border-box;margin:0;padding:0}");
        sb.AppendLine("body{font-family:'Segoe UI',Arial,sans-serif;background:#0D1117;color:#E6EDF3;padding:32px}");
        sb.AppendLine("h1{font-size:2rem;margin-bottom:8px;color:#58A6FF}");
        sb.AppendLine("h2{font-size:1.2rem;margin:24px 0 12px;color:#8B949E;text-transform:uppercase;letter-spacing:.08em}");
        sb.AppendLine(".badge{display:inline-block;padding:4px 12px;border-radius:12px;font-size:.85rem;font-weight:600}");
        sb.AppendLine(".card{background:#161B22;border:1px solid #30363D;border-radius:8px;padding:20px;margin-bottom:20px}");
        sb.AppendLine(".grid-2{display:grid;grid-template-columns:1fr 1fr;gap:16px}");
        sb.AppendLine(".kv{display:flex;justify-content:space-between;padding:6px 0;border-bottom:1px solid #21262D}");
        sb.AppendLine(".kv .k{color:#8B949E;font-size:.9rem}.kv .v{font-weight:500}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;font-size:.9rem}");
        sb.AppendLine("th{background:#21262D;color:#8B949E;text-align:left;padding:10px 12px;font-weight:600}");
        sb.AppendLine("td{padding:9px 12px;border-bottom:1px solid #21262D}");
        sb.AppendLine("tr:hover td{background:#1C2128}");
        sb.AppendLine(".ok{color:#3FB950}.fail{color:#F85149}.warn{color:#D29922}.skip{color:#8B949E}");
        sb.AppendLine(".progress-bar{background:#21262D;border-radius:4px;height:8px}");
        sb.AppendLine(".progress-fill{height:8px;border-radius:4px;background:#2F81F7}");
        sb.AppendLine("</style></head><body>");

        // Header
        sb.AppendLine($"<h1>⚡ WEDM Deployment Report</h1>");
        sb.AppendLine($"<p style='color:#8B949E;margin-bottom:24px'>{report.DeploymentName} &mdash; {report.MachineName}</p>");

        // Status badge
        sb.AppendLine($"<span class='badge' style='background:{statusColor}20;color:{statusColor};border:1px solid {statusColor}'>{report.FinalStatus}</span>");

        // Summary card
        sb.AppendLine("<div class='card' style='margin-top:24px'>");
        sb.AppendLine("<h2>Summary</h2>");
        sb.AppendLine("<div class='grid-2'>");
        KvRow(sb, "Environment",    report.Environment);
        KvRow(sb, "WebLogic",       report.Version.ToString());
        KvRow(sb, "Platform",       report.Platform);
        KvRow(sb, "OS Version",     report.OsVersion);
        KvRow(sb, "Started",        report.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        KvRow(sb, "Duration",       report.TotalDuration?.ToString(@"hh\:mm\:ss") ?? "—");
        KvRow(sb, "Steps Total",    report.TotalSteps.ToString());
        KvRow(sb, "Steps Passed",   report.StepsSucceeded.ToString());
        KvRow(sb, "Steps Failed",   report.StepsFailed.ToString());
        KvRow(sb, "Success Rate",   $"{report.SuccessRate:F1}%");
        KvRow(sb, "Executed By",    report.ExecutedBy);
        KvRow(sb, "Middleware Home", report.MiddlewareHome);
        sb.AppendLine("</div></div>");

        // Steps table
        sb.AppendLine("<div class='card'><h2>Deployment Steps</h2><table>");
        sb.AppendLine("<tr><th>#</th><th>Step</th><th>Status</th><th>Duration</th><th>Exit Code</th><th>Notes</th></tr>");
        foreach (var step in report.Steps)
        {
            var cls = step.Status switch
            {
                Domain.Enums.StepStatus.Succeeded => "ok",
                Domain.Enums.StepStatus.Failed    => "fail",
                Domain.Enums.StepStatus.Skipped   => "skip",
                _                                  => "warn"
            };
            var icon = step.Status switch
            {
                Domain.Enums.StepStatus.Succeeded => "✔",
                Domain.Enums.StepStatus.Failed    => "✘",
                Domain.Enums.StepStatus.Skipped   => "⊘",
                _                                  => "⏳"
            };
            var note = string.IsNullOrWhiteSpace(step.ErrorMessage) ? step.OutputLog : step.ErrorMessage;
            if (note?.Length > 80) note = note[..80] + "…";
            note = SecretRedactor.Redact(note ?? string.Empty);
            sb.AppendLine($"<tr><td>{step.Sequence}</td><td>{step.Name}</td>" +
                          $"<td class='{cls}'>{icon} {step.Status}</td>" +
                          $"<td>{step.Duration?.TotalSeconds:F1}s</td>" +
                          $"<td>{step.ExitCode}</td><td style='color:#8B949E'>{note}</td></tr>");
        }
        sb.AppendLine("</table></div>");

        // Footer
        sb.AppendLine($"<p style='color:#30363D;font-size:.8rem;margin-top:32px'>Generated by WEDM v1.0 &nbsp;|&nbsp; Report ID: {report.ReportId}</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();

        static void KvRow(StringBuilder sb, string k, string v)
            => sb.AppendLine($"<div class='kv'><span class='k'>{k}</span><span class='v'>{v}</span></div>");
    }

    public void Dispose()
    {
        _logger?.Dispose();
        _logger = null;
    }
}
