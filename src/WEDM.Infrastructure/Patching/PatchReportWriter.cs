using System.Text;
using System.Text.Json;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Patching;

public sealed class PatchReportWriter : IPatchReportWriter
{
    public Task WriteHtmlAsync(PatchReport report, string outputPath, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang='en'><head><meta charset='utf-8'/>");
        sb.AppendLine("<title>WEDM OPatch Report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial;margin:24px;background:#0d1117;color:#e6edf3}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin-top:16px}");
        sb.AppendLine("th,td{border:1px solid #30363d;padding:8px;text-align:left}");
        sb.AppendLine("th{background:#161b22;color:#8b949e}</style></head><body>");
        sb.AppendLine($"<h1>OPatch compliance report</h1>");
        sb.AppendLine($"<p>Machine: {System.Net.WebUtility.HtmlEncode(report.MachineName)} | Generated: {report.GeneratedAt:u}</p>");
        sb.AppendLine($"<p>ORACLE_HOME: {System.Net.WebUtility.HtmlEncode(report.OracleHome)}</p>");
        sb.AppendLine($"<p>OPatch version output: <pre>{System.Net.WebUtility.HtmlEncode(report.OpatchVersion)}</pre></p>");
        sb.AppendLine($"<p>Staging: {System.Net.WebUtility.HtmlEncode(report.StagingPath)} | Apply success: {report.ApplySucceeded}</p>");
        if (report.StagingValidationNotes.Count > 0)
        {
            sb.AppendLine("<h2>Staging validation</h2><ul>");
            foreach (var n in report.StagingValidationNotes)
                sb.AppendLine($"<li>{System.Net.WebUtility.HtmlEncode(n)}</li>");
            sb.AppendLine("</ul>");
        }

        void PatchTable(string title, List<AppliedPatchRecord> rows)
        {
            sb.AppendLine($"<h2>{title}</h2><table><tr><th>Patch ID</th><th>Applied on</th></tr>");
            foreach (var r in rows)
                sb.AppendLine($"<tr><td>{System.Net.WebUtility.HtmlEncode(r.PatchId)}</td><td>{System.Net.WebUtility.HtmlEncode(r.AppliedOn ?? "")}</td></tr>");
            sb.AppendLine("</table>");
        }

        PatchTable("Before", report.PatchesBefore);
        PatchTable("After", report.PatchesAfter);

        sb.AppendLine("<h2>Apply log summary</h2><pre style='white-space:pre-wrap;background:#161b22;padding:12px;border-radius:8px'>");
        sb.AppendLine(System.Net.WebUtility.HtmlEncode(report.ApplyLogSummary));
        sb.AppendLine("</pre>");
        sb.AppendLine("</body></html>");
        return File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8, cancellationToken);
    }

    public Task WriteJsonAsync(PatchReport report, string outputPath, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        return File.WriteAllTextAsync(outputPath, json, Encoding.UTF8, cancellationToken);
    }
}
