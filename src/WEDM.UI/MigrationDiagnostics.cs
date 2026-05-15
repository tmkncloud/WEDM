using System.Diagnostics;
using System.IO;

namespace WEDM.UI;

/// <summary>Migration workflow tracing — complements startup diagnostics.</summary>
public static class MigrationDiagnostics
{
    private static readonly object FileLock = new();
    private static string TracePath => Path.Combine(StartupDiagnostics.LogsDirectory, "migration-trace.log");

    public static void Trace(string phase, string? detail = null)
    {
        var line = $"{DateTimeOffset.Now:u} | {phase}";
        if (!string.IsNullOrWhiteSpace(detail))
            line += " | " + detail.ReplaceLineEndings(" ");

        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(StartupDiagnostics.LogsDirectory);
                File.AppendAllText(TracePath, line + Environment.NewLine);
            }
            catch
            {
                /* best effort */
            }
        }

        Debug.WriteLine(line);
    }

    public static void TraceTiming(string operation, long elapsedMs, string? detail = null)
    {
        var msg = $"{operation} completed in {elapsedMs} ms";
        if (!string.IsNullOrWhiteSpace(detail))
            msg += $" — {detail}";
        Trace("Timing", msg);
    }

    public static void TraceWorkflowPhase(string fromStep, string toStep, string? mode = null)
    {
        var detail = string.IsNullOrWhiteSpace(mode)
            ? $"{fromStep} → {toStep}"
            : $"[{mode}] {fromStep} → {toStep}";
        Trace("WorkflowPhase", detail);
    }

    public static void TraceAssessmentSummary(MigrationReadinessLogSummary summary)
    {
        Trace("Assessment",
            $"readiness={summary.ReadinessPercent:F1}% complexity={summary.Complexity} blockers={summary.BlockerCount} warnings={summary.WarningCount}");
    }

    public static void TraceDiscoveryStage(string stage, string status, long? elapsedMs = null, string? detail = null)
    {
        var msg = $"{stage}={status}";
        if (elapsedMs.HasValue)
            msg += $" ({elapsedMs} ms)";
        if (!string.IsNullOrWhiteSpace(detail))
            msg += $" — {detail}";
        Trace("DiscoveryStage", msg);
    }

    public static void TraceDiscoveryWarnings(IEnumerable<string> warnings)
    {
        foreach (var w in warnings.Take(20))
            Trace("DiscoveryWarning", w);
    }
}

public sealed record MigrationReadinessLogSummary(
    double ReadinessPercent,
    string Complexity,
    int BlockerCount,
    int WarningCount);
