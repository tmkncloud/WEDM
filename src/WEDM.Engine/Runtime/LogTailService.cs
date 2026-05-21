using System.Runtime.CompilerServices;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Runtime;

/// <summary>
/// Streams log entries from a component's primary log file in real time.
///
/// Behaviour:
///   • Tail mode — starts reading from the current end of file; does not replay history.
///   • Incremental — polls the file every <see cref="PollInterval"/> for new content.
///   • Rotation-safe — if the file is recreated (truncated / new inode), the stream
///     automatically follows the new file.
///   • Cancellation-safe — cancelling the token stops the stream without throwing.
///   • Empty stream — when the log file does not exist the method yields nothing and
///     retries on each poll, so newly created files are picked up automatically.
///
/// Error classification:
///   Lines containing "ERROR", "FATAL", "Exception", or "SEVERE" are flagged
///   as <see cref="LogTailEntry.IsError"/> = true for UI highlighting.
/// </summary>
public sealed class LogTailService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private static readonly string[] ErrorMarkers =
        ["ERROR", "FATAL", "Exception", "SEVERE", "BEA-", "<Error>"];

    private readonly ILoggingService _log;

    public LogTailService(ILoggingService log) => _log = log;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Yields <see cref="LogTailEntry"/> items as new lines are appended to the
    /// component's log file.  Returns an empty stream (but keeps polling) when
    /// the log file does not yet exist.
    /// </summary>
    public async IAsyncEnumerable<LogTailEntry> TailLogAsync(
        RuntimeComponent                     component,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var logFile = component.LogFile;
        if (string.IsNullOrWhiteSpace(logFile))
        {
            _log.Verbose($"[LogTail] {component.Name}: no log file path configured.", "Runtime");
            yield break;
        }

        _log.Verbose($"[LogTail] {component.Name}: tailing {logFile}", "Runtime");

        long   position      = -1;     // -1 = "seek to end on first open"
        long   lastFileSize  = 0;
        string currentPath   = logFile;

        while (!cancellationToken.IsCancellationRequested)
        {
            // ── Wait for next poll tick ────────────────────────────────────────
            try { await Task.Delay(PollInterval, cancellationToken); }
            catch (OperationCanceledException) { yield break; }

            // ── Resolve current log file (rotation detection) ──────────────────
            var activePath = ResolveActivePath(logFile);
            if (activePath is null)
            {
                // File not yet created — keep polling silently
                position = -1;
                continue;
            }

            // ── Detect rotation: file was replaced / truncated ─────────────────
            long fileSize;
            try { fileSize = new FileInfo(activePath).Length; }
            catch { continue; }

            bool rotated = !activePath.Equals(currentPath, StringComparison.OrdinalIgnoreCase)
                        || (position >= 0 && fileSize < lastFileSize);

            if (rotated)
            {
                _log.Verbose($"[LogTail] {component.Name}: log rotation detected — following {activePath}", "Runtime");
                currentPath = activePath;
                position    = -1;       // seek to beginning of new file
            }

            lastFileSize = fileSize;

            // ── Open and read new content ──────────────────────────────────────
            List<string>? lines = null;
            try
            {
                (lines, position) = ReadNewLines(activePath, position);
            }
            catch (IOException)
            {
                // File locked or deleted between existence check and open — retry next tick
                continue;
            }
            catch (Exception ex)
            {
                _log.Verbose($"[LogTail] {component.Name}: read error — {ex.Message}", "Runtime");
                continue;
            }

            if (lines is null) continue;

            // ── Yield new lines ────────────────────────────────────────────────
            foreach (var line in lines)
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                yield return new LogTailEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Line      = line,
                    IsError   = IsErrorLine(line),
                    Source    = component.Name,
                };
            }
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the path of the active log file to read, handling common WebLogic
    /// patterns where the log name is fixed but an older log may have been renamed.
    /// Returns null when no readable file exists.
    /// </summary>
    private static string? ResolveActivePath(string logFile)
    {
        if (File.Exists(logFile)) return logFile;

        // WebLogic sometimes writes to <name>.log00001 during rotation
        var dir  = Path.GetDirectoryName(logFile);
        var stem = Path.GetFileNameWithoutExtension(logFile);
        if (dir is null) return null;

        try
        {
            // Pick the most-recently-modified .log* file that starts with the stem
            var candidate = Directory
                .GetFiles(dir, stem + "*.log*", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();

            return candidate?.FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the log file with shared read access, seeks to <paramref name="position"/>
    /// (or EOF when position == -1), reads all new lines, and returns the updated position.
    /// Returns (null, position) when there is nothing new to read.
    /// </summary>
    private static (List<string>? lines, long newPosition) ReadNewLines(
        string filePath, long position)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);   // safe for concurrently-written logs

        // First open: seek to current end (tail mode — no history replay)
        if (position < 0)
            return (null, stream.Length);

        // Nothing new
        if (stream.Length <= position)
            return (null, position);

        stream.Seek(position, SeekOrigin.Begin);

        var lines = new List<string>();
        using var reader = new StreamReader(stream,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        string? line;
        while ((line = reader.ReadLine()) is not null)
            lines.Add(line);

        return (lines.Count > 0 ? lines : null, stream.Position);
    }

    /// <summary>
    /// Returns true when the line contains known Oracle/WebLogic error markers.
    /// This is a fast string scan — not a full log-level parser.
    /// </summary>
    private static bool IsErrorLine(string line)
    {
        foreach (var marker in ErrorMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
