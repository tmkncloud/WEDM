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

    public void StepFailed(string stepName, string error, int exitCode, Exception? ex = null, string? details = null)
    {
        var msg = $"✘  Step FAILED: {stepName}  [exit={exitCode}]  {error}";
        Write(LogLevel.Error, msg, "Workflow", ex, details: details);

        if (string.IsNullOrWhiteSpace(details)) return;

        foreach (var line in details.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var level = line.StartsWith("•", StringComparison.Ordinal) ? LogLevel.Error : LogLevel.Info;
            Write(level, line, "Validation");
        }
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
        sb.AppendLine($"<title>WEDM Report — {HtmlEncode(report.DeploymentName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("*{box-sizing:border-box;margin:0;padding:0}");
        sb.AppendLine("body{font-family:'Segoe UI',Arial,sans-serif;background:#0D1117;color:#E6EDF3;padding:32px;max-width:1400px;margin:0 auto}");
        sb.AppendLine("h1{font-size:2rem;margin-bottom:8px;color:#58A6FF}");
        sb.AppendLine("h2{font-size:1.1rem;margin:0 0 14px;color:#8B949E;text-transform:uppercase;letter-spacing:.08em}");
        sb.AppendLine(".badge{display:inline-block;padding:5px 14px;border-radius:14px;font-size:.9rem;font-weight:700}");
        sb.AppendLine(".card{background:#161B22;border:1px solid #30363D;border-radius:8px;padding:20px 24px;margin-bottom:20px}");
        sb.AppendLine(".grid-2{display:grid;grid-template-columns:1fr 1fr;gap:0 40px}");
        sb.AppendLine(".kv{display:flex;justify-content:space-between;align-items:center;padding:7px 0;border-bottom:1px solid #21262D;font-size:.9rem}");
        sb.AppendLine(".kv:last-child{border-bottom:none}");
        sb.AppendLine(".kv .k{color:#8B949E}.kv .v{font-weight:500;text-align:right;word-break:break-all;max-width:55%}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;font-size:.88rem}");
        sb.AppendLine("th{background:#21262D;color:#8B949E;text-align:left;padding:10px 12px;font-weight:600;white-space:nowrap}");
        sb.AppendLine("td{padding:9px 12px;border-bottom:1px solid #21262D;vertical-align:top}");
        sb.AppendLine("tr:hover td{background:#1C2128}");
        sb.AppendLine(".ok{color:#3FB950}.fail{color:#F85149}.warn{color:#D29922}.skip{color:#8B949E}.rbk{color:#A371F7}");
        sb.AppendLine(".finding{margin-bottom:16px;padding-bottom:14px;border-bottom:1px solid #21262D}");
        sb.AppendLine(".finding:last-child{border-bottom:none;margin-bottom:0}");
        sb.AppendLine(".finding-hdr{display:flex;align-items:center;gap:8px;margin-bottom:6px}");
        sb.AppendLine(".finding h3{font-size:.95rem;margin:0}");
        sb.AppendLine(".finding p{margin:3px 0;font-size:.88rem;color:#C9D1D9;line-height:1.5}");
        sb.AppendLine(".finding .rem{background:#1C2128;border-left:3px solid #2188FF;padding:6px 10px;margin-top:6px;font-size:.85rem;color:#8B949E;border-radius:0 4px 4px 0}");
        sb.AppendLine(".pill{display:inline-block;padding:2px 8px;border-radius:8px;font-size:.75rem;font-weight:600;margin-left:4px}");
        sb.AppendLine(".retry-badge{background:#D2992220;color:#D29922;border:1px solid #D2992260}");
        sb.AppendLine(".section-empty{color:#8B949E;font-style:italic;font-size:.9rem;padding:8px 0}");
        sb.AppendLine("</style></head><body>");

        // ── Page header ────────────────────────────────────────────────────────
        sb.AppendLine($"<h1>⚡ WEDM Deployment Report</h1>");
        sb.AppendLine($"<p style='color:#8B949E;margin-bottom:16px'>{HtmlEncode(report.DeploymentName)} &mdash; {HtmlEncode(report.MachineName)}</p>");
        sb.AppendLine($"<span class='badge' style='background:{statusColor}20;color:{statusColor};border:1px solid {statusColor}'>{report.FinalStatus}</span>");

        // ── Summary card ───────────────────────────────────────────────────────
        sb.AppendLine("<div class='card' style='margin-top:24px'><h2>Deployment Summary</h2><div class='grid-2'>");
        KvRow(sb, "Environment",     report.Environment);
        KvRow(sb, "WebLogic",        report.Version.ToString());
        KvRow(sb, "Platform",        report.Platform);
        KvRow(sb, "OS Version",      report.OsVersion);
        KvRow(sb, "Started",         report.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        KvRow(sb, "Duration",        report.TotalDuration?.ToString(@"hh\:mm\:ss") ?? "—");
        KvRow(sb, "Steps Total",     report.TotalSteps.ToString());
        KvRow(sb, "Steps Succeeded", report.StepsSucceeded.ToString());
        KvRow(sb, "Steps Failed",    report.StepsFailed.ToString());
        KvRow(sb, "Success Rate",    $"{report.SuccessRate:F1}%");
        KvRow(sb, "Executed By",     report.ExecutedBy);
        KvRow(sb, "Middleware Home", report.MiddlewareHome);
        if (!string.IsNullOrWhiteSpace(report.DomainHome))
            KvRow(sb, "Domain Home", report.DomainHome);
        if (!string.IsNullOrWhiteSpace(report.AdminUrl))
            KvRow(sb, "Admin Console", report.AdminUrl);
        if (!string.IsNullOrWhiteSpace(report.InstalledJdkPath))
            KvRow(sb, "JDK Path", report.InstalledJdkPath);
        sb.AppendLine("</div></div>");

        AppendLocalPayloadSection(sb, report);
        AppendJdkInstallationSection(sb, report);

        // ── Failed prerequisites (errors + fatals) ─────────────────────────────
        AppendValidationSection(sb, report, errorsOnly: true);

        // ── Warning prerequisites ──────────────────────────────────────────────
        AppendValidationSection(sb, report, errorsOnly: false);

        // ── Steps table ────────────────────────────────────────────────────────
        sb.AppendLine("<div class='card'><h2>Deployment Steps</h2><table>");
        sb.AppendLine("<tr><th>#</th><th>Step</th><th>Category</th><th>Status</th><th>Attempts</th><th>Duration</th><th>Details</th></tr>");
        foreach (var step in report.Steps.OrderBy(s => s.Sequence))
        {
            var (cls, icon) = step.Status switch
            {
                Domain.Enums.StepStatus.Succeeded      => ("ok",   "✔"),
                Domain.Enums.StepStatus.Failed         => ("fail", "✘"),
                Domain.Enums.StepStatus.Skipped        => ("skip", "⊘"),
                Domain.Enums.StepStatus.RolledBack     => ("rbk",  "↩"),
                Domain.Enums.StepStatus.RollbackFailed => ("fail", "⚠"),
                _                                       => ("warn", "⏳")
            };
            var note = step.Status == Domain.Enums.StepStatus.Failed
                ? (string.IsNullOrWhiteSpace(step.ErrorMessage) ? step.OutputLog : step.ErrorMessage)
                : step.OutputLog;
            note = note?.Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
            if (note.Length > 250) note = note[..250] + "…";
            note = SecretRedactor.Redact(note);

            var retryBadge = step.AttemptCount > 1
                ? $"<span class='pill retry-badge'>{step.AttemptCount} attempts</span>"
                : string.Empty;

            sb.AppendLine($"<tr>" +
                $"<td>{step.Sequence}</td>" +
                $"<td>{HtmlEncode(step.Name)}{retryBadge}</td>" +
                $"<td style='color:#8B949E'>{HtmlEncode(step.Category)}</td>" +
                $"<td class='{cls}'>{icon} {step.Status}</td>" +
                $"<td style='text-align:center'>{step.AttemptCount}</td>" +
                $"<td style='white-space:nowrap'>{step.Duration?.TotalSeconds:F1}s</td>" +
                $"<td style='color:#8B949E;font-size:.82rem'>{HtmlEncode(note)}</td>" +
                $"</tr>");
        }
        sb.AppendLine("</table></div>");

        // ── Rollback summary ───────────────────────────────────────────────────
        AppendRollbackSection(sb, report);

        // ── Footer ─────────────────────────────────────────────────────────────
        sb.AppendLine($"<p style='color:#30363D;font-size:.78rem;margin-top:32px'>" +
            $"Generated by WEDM v1.0 &nbsp;|&nbsp; Report ID: {report.ReportId} &nbsp;|&nbsp; " +
            $"Config ID: {report.ConfigurationId}" +
            $"</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();

        // ── Local helpers ──────────────────────────────────────────────────────

        static void KvRow(StringBuilder sb, string k, string v)
            => sb.AppendLine($"<div class='kv'><span class='k'>{k}</span><span class='v'>{HtmlEncode(v)}</span></div>");

        static void AppendValidationSection(StringBuilder sb, DeploymentReport report, bool errorsOnly)
        {
            if (report.Validation is null) return;

            var findings = errorsOnly
                ? report.Validation.Findings
                    .Where(f => !f.Passed && f.Severity >= ValidationSeverity.Error)
                    .OrderByDescending(f => f.Severity)
                    .ThenBy(f => f.CheckName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : report.Validation.Findings
                    .Where(f => !f.Passed && f.Severity == ValidationSeverity.Warning)
                    .OrderBy(f => f.CheckName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (findings.Count == 0) return;

            var (heading, borderColor) = errorsOnly
                ? ("Failed Prerequisites — Deployment Blocked", "#F85149")
                : ("Prerequisite Warnings", "#D29922");

            sb.AppendLine($"<div class='card' style='border-color:{borderColor}60'><h2 style='color:{borderColor}'>{heading}</h2>");
            foreach (var f in findings)
            {
                var severityColor = f.Severity == ValidationSeverity.Fatal ? "#FF0000" : "#F85149";
                var warnColor = "#D29922";
                var color = errorsOnly ? severityColor : warnColor;
                var sev = f.Severity.ToString().ToUpper();

                sb.AppendLine($"<div class='finding'>");
                sb.AppendLine($"<div class='finding-hdr'><span style='color:{color};font-weight:700'>[{sev}]</span><h3>{HtmlEncode(f.CheckName)}</h3></div>");
                sb.AppendLine($"<p>{HtmlEncode(SecretRedactor.Redact(f.Message))}</p>");

                if (f.ExpectedValue is not null || f.ActualValue is not null)
                {
                    sb.Append("<p style='font-size:.84rem;color:#8B949E'>");
                    if (f.ExpectedValue is not null)
                        sb.Append($"<strong>Expected:</strong> {HtmlEncode(SecretRedactor.Redact(f.ExpectedValue.ToString()))}");
                    if (f.ExpectedValue is not null && f.ActualValue is not null) sb.Append("&nbsp;&nbsp;|&nbsp;&nbsp;");
                    if (f.ActualValue is not null)
                        sb.Append($"<strong>Actual:</strong> {HtmlEncode(SecretRedactor.Redact(f.ActualValue.ToString()))}");
                    sb.AppendLine("</p>");
                }

                if (!string.IsNullOrWhiteSpace(f.Remediation))
                    sb.AppendLine($"<div class='rem'>💡 {HtmlEncode(SecretRedactor.Redact(f.Remediation))}</div>");

                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        static void AppendRollbackSection(StringBuilder sb, DeploymentReport report)
        {
            var rollback = report.Rollback;
            if (rollback is null) return;

            var headerColor = rollback.FullyRolledBack ? "#A371F7"
                : rollback.StepsFailed > 0             ? "#F85149"
                :                                        "#D29922";
            var headerText = rollback.FullyRolledBack ? "Rollback — Completed"
                : rollback.StepsRolledBack > 0         ? "Rollback — Partial"
                :                                        "Rollback — Failed";

            sb.AppendLine($"<div class='card' style='border-color:{headerColor}60'>");
            sb.AppendLine($"<h2 style='color:{headerColor}'>{headerText}</h2>");

            // Rollback summary stats
            sb.AppendLine("<div class='grid-2' style='margin-bottom:16px'>");
            KvRow(sb, "Started",       rollback.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            KvRow(sb, "Duration",      rollback.Duration?.ToString(@"mm\:ss\.f") ?? "—");
            KvRow(sb, "Reversed",      rollback.StepsRolledBack.ToString());
            KvRow(sb, "No Executor",   rollback.StepsNoExecutor.ToString());
            KvRow(sb, "Failed",        rollback.StepsFailed.ToString());
            KvRow(sb, "Manual follow-up", rollback.StepsManualInterventionRequired.ToString());
            sb.AppendLine("</div>");

            if (rollback.StepsNoExecutor > 0)
            {
                sb.AppendLine("<p style='color:#D29922;font-size:.88rem;margin-bottom:12px'>" +
                    "⚠ Steps marked 'NoExecutor' were not automatically reversed — manual operator intervention required.</p>");
            }

            // Rollback step records
            if (rollback.Records.Count > 0)
            {
                sb.AppendLine("<table><tr><th>Seq</th><th>Step</th><th>Rollback Action</th><th>Outcome</th><th>Duration</th><th>Details</th></tr>");
                foreach (var r in rollback.Records)
                {
                    var (cls, icon) = r.Outcome switch
                    {
                        "RolledBack"                    => ("rbk",  "↩"),
                        "ManualInterventionRequired"    => ("warn", "!"),
                        "NoExecutor"                    => ("warn", "⚠"),
                        "Failed"                        => ("fail", "✘"),
                        "Exception"                     => ("fail", "💥"),
                        _                               => ("skip", "?")
                    };
                    var detail = SecretRedactor.Redact(
                        string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error);
                    if (detail.Length > 200) detail = detail[..200] + "…";

                    sb.AppendLine($"<tr>" +
                        $"<td>{r.Sequence}</td>" +
                        $"<td>{HtmlEncode(r.StepName)}</td>" +
                        $"<td style='color:#8B949E;font-size:.82rem'>{HtmlEncode(r.RollbackAction)}</td>" +
                        $"<td class='{cls}'>{icon} {r.Outcome}</td>" +
                        $"<td style='white-space:nowrap'>{r.Duration.TotalSeconds:F1}s</td>" +
                        $"<td style='color:#8B949E;font-size:.82rem'>{HtmlEncode(detail)}</td>" +
                        $"</tr>");
                }
                sb.AppendLine("</table>");
            }
            sb.AppendLine("</div>");
        }
    }

    private static void AppendLocalPayloadSection(StringBuilder sb, DeploymentReport report)
    {
        var lp = report.LocalPayload;
        if (lp is null || !lp.UsedLocalRepository) return;

        sb.AppendLine("<div class='card'><h2>Local Payload Repository</h2>");
        KvRowStatic(sb, "Repository root", lp.RepositoryRoot);
        KvRowStatic(sb, "Version folder", lp.VersionFolder);
        KvRowStatic(sb, "Manifest", lp.ManifestPresent ? "payload-manifest.json present" : "Not present (checksum warnings only)");

        if (lp.Entries.Count == 0)
        {
            sb.AppendLine("<p class='section-empty'>No payload entries recorded.</p></div>");
            return;
        }

        sb.AppendLine("<table><tr><th>Component</th><th>Resolved path</th><th>Checksum</th><th>Status</th></tr>");
        foreach (var e in lp.Entries)
        {
            var chk = e.ChecksumStatus switch
            {
                Domain.Models.PayloadChecksumStatus.Verified      => ("ok", "Verified"),
                Domain.Models.PayloadChecksumStatus.Mismatch        => ("fail", "Mismatch"),
                Domain.Models.PayloadChecksumStatus.ManifestMissing => ("warn", "No manifest"),
                _                                                 => ("skip", e.ChecksumStatus.ToString())
            };
            var path = string.IsNullOrWhiteSpace(e.ResolvedPath) ? "—" : e.ResolvedPath;
            var status = e.Found ? "Resolved" : "Missing";
            var statusCls = e.Found ? "ok" : "fail";
            sb.AppendLine($"<tr><td>{HtmlEncode(e.Component)}</td>" +
                $"<td style='word-break:break-all'>{HtmlEncode(path)}</td>" +
                $"<td class='{chk.Item1}'>{chk.Item2}</td>" +
                $"<td class='{statusCls}'>{status}</td></tr>");
        }
        sb.AppendLine("</table></div>");
    }

    private static void AppendJdkInstallationSection(StringBuilder sb, DeploymentReport report)
    {
        var jdk = report.JdkInstallation;
        if (jdk is null) return;

        sb.AppendLine("<div class='card'><h2>JDK Installation</h2>");
        KvRowStatic(sb, "Installer type", jdk.InstallerType);
        KvRowStatic(sb, "Installer path", jdk.InstallerPath);
        KvRowStatic(sb, "Target JAVA_HOME", jdk.TargetJavaHome);
        KvRowStatic(sb, "Command line", jdk.ArgumentsDisplay);
        KvRowStatic(sb, "Raw exit code", jdk.RawExitCode.ToString());
        KvRowStatic(sb, "Normalized status", $"{jdk.NormalizedStatus}: {jdk.NormalizedMessage}");
        if (!string.IsNullOrWhiteSpace(jdk.ResolvedJavaHome))
            KvRowStatic(sb, "Resolved JAVA_HOME", jdk.ResolvedJavaHome);
        if (!string.IsNullOrWhiteSpace(jdk.JavaVersionOutput))
            KvRowStatic(sb, "java -version", jdk.JavaVersionOutput.Trim());

        if (jdk.ValidationChecks.Count > 0)
        {
            sb.AppendLine("<table style='margin-top:12px'><tr><th>Validation check</th></tr>");
            foreach (var c in jdk.ValidationChecks)
            {
                var cls = c.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) ? "ok"
                    : c.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) ? "fail" : "warn";
                sb.AppendLine($"<tr><td class='{cls}'>{HtmlEncode(c)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        sb.AppendLine("</div>");
    }

    private static void KvRowStatic(StringBuilder sb, string k, string v)
        => sb.AppendLine($"<div class='kv'><span class='k'>{HtmlEncode(k)}</span><span class='v'>{HtmlEncode(v)}</span></div>");

    private static string HtmlEncode(object? value)
        => System.Net.WebUtility.HtmlEncode(value?.ToString() ?? string.Empty);

    public void Dispose()
    {
        _logger?.Dispose();
        _logger = null;
    }
}
