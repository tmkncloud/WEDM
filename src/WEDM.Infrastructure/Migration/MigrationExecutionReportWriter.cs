using System.Net;
using System.Text;
using System.Text.Json;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Migration;

public sealed class MigrationExecutionReportWriter : IMigrationExecutionReportWriter
{
    public async Task<string> WriteJsonAsync(
        MigrationConfiguration configuration,
        MigrationExecutionResult result,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"execution-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        var model = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            project        = configuration.Name,
            result.SessionId,
            result.Outcome,
            result.DryRun,
            result.TotalDurationMs,
            result.Preflight,
            stages           = result.Stages,
            checkpoints      = result.Checkpoints,
            operatorApprovals = result.OperatorApprovals,
            wlstExecutions   = result.WlstExecutions,
            postValidation   = result.PostValidation,
            rollbackManifest = result.RollbackManifest,
            executionLog     = result.ExecutionLog,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(model, MigrationJsonOptions.Create()), cancellationToken);
        return path;
    }

    public async Task<string> WriteHtmlAsync(
        MigrationConfiguration configuration,
        MigrationExecutionResult result,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"execution-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.html");
        await File.WriteAllTextAsync(path, BuildHtml(configuration, result), Encoding.UTF8, cancellationToken);
        return path;
    }

    private static string BuildHtml(MigrationConfiguration config, MigrationExecutionResult result)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'><title>WEDM Execution Report</title>");
        sb.Append("<style>body{font-family:Segoe UI,Arial;margin:24px;background:#0d1117;color:#e6edf3}");
        sb.Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #30363d;padding:8px}th{background:#21262d}</style></head><body>");
        sb.Append($"<h1>Migration Execution Report</h1><p>{E(config.Name)} · {E(result.Outcome.ToString())} · {(result.DryRun ? "DRY-RUN" : "LIVE")}</p>");
        sb.Append($"<p>Duration: {result.TotalDurationMs} ms</p>");
        sb.Append("<h2>Stages</h2><table><tr><th>Stage</th><th>Status</th><th>Duration</th></tr>");
        foreach (var s in result.Stages)
            sb.Append($"<tr><td>{E(s.DisplayName)}</td><td>{E(s.Status.ToString())}</td><td>{s.DurationMs} ms</td></tr>");
        sb.Append("</table><h2>WLST executions</h2><table><tr><th>Script</th><th>Success</th><th>Exit</th></tr>");
        foreach (var w in result.WlstExecutions)
            sb.Append($"<tr><td>{E(w.ScriptName)}</td><td>{w.Success}</td><td>{w.ExitCode}</td></tr>");
        sb.Append("</table><h2>Operator approvals</h2><ul>");
        foreach (var a in result.OperatorApprovals)
            sb.Append($"<li>{E(a.Checkpoint.ToString())}: {E(a.Decision.ToString())} ({a.TimestampUtc:u})</li>");
        sb.Append("</ul></body></html>");
        return sb.ToString();
    }
}
