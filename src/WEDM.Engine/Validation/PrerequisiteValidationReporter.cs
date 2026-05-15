using System.Text;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Validation;

/// <summary>Formats and emits structured prerequisite validation findings for operators.</summary>
public static class PrerequisiteValidationReporter
{
    public static IReadOnlyList<ValidationFinding> GetBlockingFindings(PrerequisiteValidationResult result)
        => result.Findings
            .Where(f => !f.Passed && f.Severity >= ValidationSeverity.Error)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.CheckName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string FormatSummary(PrerequisiteValidationResult result)
        => $"{result.PassCount} passed, {result.WarnCount} warnings, {result.ErrorCount} errors, {result.Fatals} fatal";

    public static string FormatDetailedBlockers(PrerequisiteValidationResult result)
    {
        var blockers = GetBlockingFindings(result);
        if (blockers.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Failed validations:");
        foreach (var f in blockers)
            AppendFinding(sb, f);
        return sb.ToString().TrimEnd();
    }

    public static void LogBlockingFindings(ILoggingService log, PrerequisiteValidationResult result, string category = "Validation")
    {
        var blockers = GetBlockingFindings(result);
        if (blockers.Count == 0) return;

        log.Error($"✘ {blockers.Count} blocking prerequisite validation(s):", category: category);
        foreach (var f in blockers)
        {
            log.Error($"  • {f.CheckName}: {f.Message}", category: category);
            if (f.ExpectedValue is not null)
                log.Info($"    Expected: {f.ExpectedValue}", category);
            if (f.ActualValue is not null)
                log.Info($"    Actual: {f.ActualValue}", category);
            if (!string.IsNullOrWhiteSpace(f.Remediation))
                log.Warning($"    Remediation: {f.Remediation}", category);
        }
    }

    /// <summary>Only database/network reachability failures are worth automatic retry.</summary>
    public static bool IsRetryRecommended(PrerequisiteValidationResult result)
    {
        var blockers = GetBlockingFindings(result);
        return blockers.Count > 0 &&
               blockers.All(f => f.CheckName.StartsWith("Database.", StringComparison.OrdinalIgnoreCase));
    }

    public static string FormatHtmlBlockers(PrerequisiteValidationResult result)
    {
        var blockers = GetBlockingFindings(result);
        if (blockers.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var f in blockers)
        {
            sb.AppendLine($"<div class='finding'><h3>{Html(f.CheckName)}</h3>");
            sb.AppendLine($"<p>{Html(f.Message)}</p>");
            if (f.ExpectedValue is not null)
                sb.AppendLine($"<p><strong>Expected:</strong> {Html(f.ExpectedValue)}</p>");
            if (f.ActualValue is not null)
                sb.AppendLine($"<p><strong>Actual:</strong> {Html(f.ActualValue)}</p>");
            if (!string.IsNullOrWhiteSpace(f.Remediation))
                sb.AppendLine($"<p><strong>Remediation:</strong> {Html(f.Remediation)}</p>");
            sb.AppendLine("</div>");
        }
        return sb.ToString();
    }

    private static void AppendFinding(StringBuilder sb, ValidationFinding f)
    {
        sb.AppendLine();
        sb.Append("• ").Append(f.CheckName).AppendLine(":");
        sb.Append("  ").AppendLine(f.Message);
        if (f.ExpectedValue is not null)
            sb.Append("  Expected: ").AppendLine(f.ExpectedValue.ToString());
        if (f.ActualValue is not null)
            sb.Append("  Actual: ").AppendLine(f.ActualValue.ToString());
        if (!string.IsNullOrWhiteSpace(f.Remediation))
            sb.Append("  Remediation: ").AppendLine(f.Remediation);
    }

    private static string Html(object? value)
        => System.Net.WebUtility.HtmlEncode(value?.ToString() ?? string.Empty);
}
