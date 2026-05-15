using System.Net;
using System.Text;
using System.Text.Json;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Migration;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Migration;

public sealed class MigrationReportWriter : IMigrationReportWriter
{
    public async Task<string> WriteJsonAsync(
        MigrationConfiguration configuration,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, BuildFileName(configuration, "json"));
        var json = JsonSerializer.Serialize(BuildReportModel(configuration), MigrationJsonOptions.Create());
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return path;
    }

    public async Task<string> WriteHtmlAsync(
        MigrationConfiguration configuration,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, BuildFileName(configuration, "html"));
        await File.WriteAllTextAsync(path, BuildHtml(configuration), Encoding.UTF8, cancellationToken);
        return path;
    }

    private static string BuildFileName(MigrationConfiguration config, string extension)
    {
        var safe = string.Concat(config.Name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').ToArray()).Trim();
        safe = string.IsNullOrWhiteSpace(safe) ? "migration-assessment" : safe.Replace(' ', '-');
        return $"{safe}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{extension}";
    }

    private static object BuildReportModel(MigrationConfiguration config) => new
    {
        generatedAtUtc = DateTimeOffset.UtcNow,
        product        = "WEDM Migration Assessment",
        project        = config.Name,
        upgradePath    = MigrationVersionMatrix.DescribeUpgradePath(config.Source.Release, config.Target.Release),
        source         = config.Source,
        target         = config.Target,
        strategy       = config.Strategy.ToString(),
        readiness      = config.Readiness,
        topology       = config.Topology,
        domainAnalysis = config.DomainAnalysis,
        oracleInventory = config.OracleInventory,
        discoveryStages = config.DiscoveryStages,
        discoveryUsedRealScan = config.DiscoveryUsedRealScan,
        discoveryWarnings = config.DiscoveryWarnings,
        transformationCompleted = config.TransformationCompleted,
        transformationWorkspace = config.TransformationWorkspacePath,
        transformationConfidence = config.Transformation?.Confidence,
        transformationArtifacts = config.Transformation?.Artifacts.Count,
        formsModernization = config.FormsModernization,
        reportsModernization = config.ReportsModernization,
        migrationPlan = config.Transformation?.MigrationPlan,
        formsMetadata  = config.FormsMetadata,
        discoveryInsights = config.DiscoveryInsights,
        compatibilityFindings = config.CompatibilityFindings,
        validationMessages = config.ValidationMessages,
        timings = new { discoveryMs = config.DiscoveryDurationMs, assessmentMs = config.AssessmentDurationMs },
    };

    private static string BuildHtml(MigrationConfiguration config)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

        var path = MigrationVersionMatrix.DescribeUpgradePath(config.Source.Release, config.Target.Release);
        var r    = config.Readiness;
        var sb   = new StringBuilder();

        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>");
        sb.Append("<title>WEDM Migration Assessment</title>");
        sb.Append("<style>");
        sb.Append("body{font-family:'Segoe UI',Arial,sans-serif;margin:0;background:#0d1117;color:#e6edf3}");
        sb.Append(".wrap{max-width:1100px;margin:0 auto;padding:32px}h1,h2{color:#58a6ff}");
        sb.Append(".hero{background:#161b22;border:1px solid #30363d;border-radius:12px;padding:28px;margin-bottom:24px}");
        sb.Append(".grid{display:grid;grid-template-columns:repeat(4,1fr);gap:16px;margin:20px 0}");
        sb.Append(".card{background:#161b22;border:1px solid #30363d;border-radius:10px;padding:18px}");
        sb.Append(".score{font-size:42px;font-weight:700;color:#3fb950}");
        sb.Append("table{width:100%;border-collapse:collapse;margin-top:12px}");
        sb.Append("th,td{border:1px solid #30363d;padding:10px}th{background:#21262d;color:#8b949e}");
        sb.Append("</style></head><body><div class=\"wrap\">");

        sb.Append("<div class=\"hero\">");
        sb.Append($"<h1>Migration Readiness Assessment</h1>");
        sb.Append($"<p><strong>{E(config.Name)}</strong> · Generated {DateTimeOffset.UtcNow:u}</p>");
        sb.Append($"<p>Upgrade path: <strong>{E(path)}</strong></p>");
        sb.Append($"<p>{E(r.ExecutiveSummary)}</p></div>");

        sb.Append("<div class=\"grid\">");
        sb.Append(MetricCard($"{r.ReadinessPercent:F1}%", "Readiness", score: true));
        sb.Append(MetricCard(E(r.Complexity.ToString()), "Complexity"));
        sb.Append(MetricCard(r.BlockerCount.ToString(), "Blockers"));
        sb.Append(MetricCard(r.WarningCount.ToString(), "Warnings"));
        sb.Append("</div>");

        sb.Append($"<h2>Technical summary</h2><p>{E(r.TechnicalSummary)}</p>");
        sb.Append("<h2>Environment topology</h2>");
        sb.Append("<table><tr><th>Domain</th><th>Admin URL</th><th>Managed servers</th><th>Clusters</th><th>Node Manager</th></tr><tr>");
        sb.Append(Cell(E(config.Topology.DomainName)));
        sb.Append(Cell(E(config.Topology.AdminServerUrl)));
        sb.Append(Cell(config.Topology.ManagedServerCount.ToString()));
        sb.Append(Cell(config.Topology.ClusterCount.ToString()));
        sb.Append(Cell(config.Topology.NodeManagerConfigured ? "Configured" : "Not configured"));
        sb.Append("</tr></table>");

        if (config.Topology.ManagedServers.Count > 0)
        {
            sb.Append("<h3>Managed servers</h3><table><tr><th>Name</th><th>Cluster</th><th>Port</th><th>State</th></tr>");
            foreach (var server in config.Topology.ManagedServers)
            {
                sb.Append("<tr>");
                sb.Append(Cell(E(server.Name)));
                sb.Append(Cell(E(server.Cluster)));
                sb.Append(Cell(server.ListenPort.ToString()));
                sb.Append(Cell(E(server.State)));
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }

        sb.Append("<h2>Oracle inventory &amp; patches</h2>");
        sb.Append("<table><tr><th>Inventory healthy</th><th>Homes</th><th>Patches</th><th>Real scan</th></tr><tr>");
        sb.Append(Cell(config.OracleInventory.InventoryHealthy ? "Yes" : "No"));
        sb.Append(Cell(config.OracleInventory.HomeCount.ToString()));
        sb.Append(Cell(config.OracleInventory.Patches.Count.ToString()));
        sb.Append(Cell(config.DiscoveryUsedRealScan ? "Yes" : "Preview"));
        sb.Append("</tr></table>");

        if (config.DomainAnalysis.DeprecatedJvmFlags.Count > 0)
        {
            sb.Append("<h3>Deprecated JVM flags</h3><ul>");
            foreach (var flag in config.DomainAnalysis.DeprecatedJvmFlags)
                sb.Append($"<li>{E(flag)}</li>");
            sb.Append("</ul>");
        }

        sb.Append("<h2>Compatibility findings</h2>");
        sb.Append("<table><tr><th>Severity</th><th>Category</th><th>Title</th><th>Description</th></tr>");
        foreach (var finding in config.CompatibilityFindings.OrderByDescending(f => f.Severity))
        {
            sb.Append("<tr>");
            sb.Append(Cell(E(finding.Severity.ToString())));
            sb.Append(Cell(E(finding.Category.ToString())));
            sb.Append(Cell(E(finding.Title)));
            sb.Append(Cell(E(finding.Description)));
            sb.Append("</tr>");
        }
        sb.Append("</table>");

        sb.Append("<h2>Target recommendation</h2>");
        sb.Append($"<p>Recommended target: <strong>{E(config.Target.DisplayName)}</strong></p>");
        sb.Append($"<p>Strategy: <strong>{E(config.Strategy.ToString())}</strong> · Effort: <strong>{E(r.EffortCategory.ToString())}</strong></p>");
        sb.Append("<p style=\"margin-top:32px;color:#8b949e;font-size:12px\">WEDM Migration Assessment Report</p>");
        sb.Append("</div></body></html>");

        return sb.ToString();
    }

    private static string MetricCard(string value, string label, bool score = false)
    {
        var valueHtml = score
            ? $"<div class=\"score\">{value}</div>"
            : $"<div style=\"font-size:28px;font-weight:700\">{value}</div>";
        return $"<div class=\"card\">{valueHtml}<div>{label}</div></div>";
    }

    private static string Cell(string value) => $"<td>{value}</td>";
}
