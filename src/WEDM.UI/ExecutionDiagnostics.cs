using System.Diagnostics;
using System.IO;

namespace WEDM.UI;

public static class ExecutionDiagnostics
{
    private static readonly object FileLock = new();
    private static string TracePath => Path.Combine(StartupDiagnostics.LogsDirectory, "migration-execution-trace.log");

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

    public static void TraceStage(string stage, string status, long? ms = null)
    {
        var msg = $"{stage}={status}";
        if (ms.HasValue) msg += $" ({ms} ms)";
        Trace("ExecutionStage", msg);
    }

    public static void TraceCheckpoint(string kind, string decision, string? note = null)
    {
        var msg = $"{kind}={decision}";
        if (!string.IsNullOrWhiteSpace(note)) msg += $" — {note}";
        Trace("Checkpoint", msg);
    }

    public static void TraceWlst(string script, bool dryRun, bool success, int exitCode)
        => Trace("WLST", $"{script} dryRun={dryRun} success={success} exit={exitCode}");
}
