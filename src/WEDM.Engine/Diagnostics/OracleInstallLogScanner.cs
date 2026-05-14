namespace WEDM.Engine.Diagnostics;

/// <summary>
/// Locates Oracle Universal Installer / cfgtoollogs artifacts after a silent install for observability.
/// </summary>
public static class OracleInstallLogScanner
{
    /// <summary>Returns the newest matching log file, if any.</summary>
    public static string? FindLatestOuiStyleLog(
        string oracleInventory,
        string middlewareHome,
        string tempDirectory)
    {
        var files = new List<(string Path, DateTime Time)>();

        void AddDir(string? dir, string pattern)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                    files.Add((f, File.GetLastWriteTimeUtc(f)));
            }
            catch { /* ignore */ }
        }

        AddDir(Path.Combine(oracleInventory, "logs"), "install*.log");
        AddDir(Path.Combine(oracleInventory, "logs"), "oraInstall*.log");
        AddDir(tempDirectory, "oraInstall*.log");
        AddDir(Path.GetTempPath(), "oraInstall*.log");

        var cfg = Path.Combine(middlewareHome, "cfgtoollogs");
        if (Directory.Exists(cfg))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(cfg, "*.log", SearchOption.AllDirectories))
                    files.Add((f, File.GetLastWriteTimeUtc(f)));
            }
            catch { /* ignore */ }
        }

        return files.OrderByDescending(t => t.Time).Select(t => t.Path).FirstOrDefault();
    }

    /// <summary>Reads the last <paramref name="maxLines"/> lines for structured logging.</summary>
    public static string ReadLogTail(string path, int maxLines = 80)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var tail = lines.Length <= maxLines ? lines : lines[^maxLines..];
            return string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex)
        {
            return $"[Could not read log tail: {ex.Message}]";
        }
    }
}
