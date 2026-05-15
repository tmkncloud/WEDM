using System.Diagnostics;
using System.IO;

namespace WEDM.UI;

public static class TransformationDiagnostics
{
    private static readonly object FileLock = new();
    private static string TracePath => Path.Combine(StartupDiagnostics.LogsDirectory, "transformation-trace.log");

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
            catch { /* best effort */ }
        }

        Debug.WriteLine(line);
    }

    public static void TraceStage(string stage, string status, long? elapsedMs = null)
    {
        var msg = $"{stage}={status}";
        if (elapsedMs.HasValue) msg += $" ({elapsedMs} ms)";
        Trace("TransformationStage", msg);
    }

    public static void TraceArtifacts(string workspace, int count, string confidence)
        => Trace("Artifacts", $"workspace={workspace} count={count} confidence={confidence}");

    public static void TraceTiming(string operation, long elapsedMs, string? detail = null)
    {
        var msg = $"{operation} completed in {elapsedMs} ms";
        if (!string.IsNullOrWhiteSpace(detail)) msg += $" — {detail}";
        Trace("Timing", msg);
    }
}
