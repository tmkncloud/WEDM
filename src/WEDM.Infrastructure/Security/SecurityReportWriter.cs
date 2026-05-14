using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Security;

public sealed class SecurityReportWriter : ISecurityReportWriter
{
    public Task WriteHtmlAsync(ComplianceReport report, string outputPath, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang='en'><head><meta charset='utf-8'/>");
        sb.AppendLine("<title>WEDM Security &amp; Compliance</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial;margin:24px;background:#0d1117;color:#e6edf3}");
        sb.AppendLine(".bar{height:10px;background:#30363d;border-radius:4px;overflow:hidden;margin:6px 0 16px}");
        sb.AppendLine(".fill{height:100%;background:linear-gradient(90deg,#238636,#2ea043)}");
        sb.AppendLine("table{border-collapse:collapse;width:100%}th,td{border:1px solid #30363d;padding:8px;text-align:left}");
        sb.AppendLine("th{background:#161b22;color:#8b949e}.pass{color:#3fb950}.fail{color:#f85149}</style></head><body>");
        sb.AppendLine("<h1>Security &amp; compliance audit</h1>");
        sb.AppendLine($"<p>Machine: {WebUtility.HtmlEncode(report.MachineName)} | Generated: {report.GeneratedAt:u}</p>");
        sb.AppendLine($"<p>Environment profile: {WebUtility.HtmlEncode(report.Environment.ToString())}</p>");

        void Bar(string label, int pct)
        {
            sb.AppendLine($"<h3>{WebUtility.HtmlEncode(label)} — {pct}%</h3><div class='bar'><div class='fill' style='width:{pct}%'></div></div>");
        }

        Bar("Overall", report.OverallScore);
        Bar("Secrets management", report.SecretsManagementScore);
        Bar("SSL readiness", report.SslReadinessScore);
        Bar("Hardening", report.HardeningScore);

        sb.AppendLine("<h2>Findings</h2><table><tr><th>ID</th><th>Category</th><th>Title</th><th>Result</th><th>Detail</th></tr>");
        foreach (var f in report.Findings)
        {
            var cls = f.Passed ? "pass" : "fail";
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(f.Id)}</td>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(f.Category)}</td>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(f.Title)}</td>");
            sb.AppendLine($"<td class='{cls}'>{(f.Passed ? "PASS" : "FAIL")}</td>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(f.Detail)}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</table></body></html>");
        return File.WriteAllTextAsync(outputPath, sb.ToString(), cancellationToken);
    }

    public Task WriteJsonAsync(ComplianceReport report, string outputPath, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
        return File.WriteAllTextAsync(outputPath, json, cancellationToken);
    }
}
