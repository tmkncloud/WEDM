namespace WEDM.Infrastructure.Security;

/// <summary>
/// Clears ephemeral secrets from memory buffers, temp files, and WLST artifacts after deployment or crash.
/// </summary>
public sealed class DeploymentSecretLifecycleService
{
    private readonly List<string> _trackedTempFiles = [];
    private readonly object _sync = new();

    public void TrackTempFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_sync) _trackedTempFiles.Add(path);
    }

    public void CleanupSession(string tempDirectory, IEnumerable<string>? extraPaths = null)
    {
        var paths = new List<string>();
        lock (_sync)
        {
            paths.AddRange(_trackedTempFiles);
            _trackedTempFiles.Clear();
        }

        if (extraPaths is not null) paths.AddRange(extraPaths);

        if (!string.IsNullOrWhiteSpace(tempDirectory) && Directory.Exists(tempDirectory))
        {
            foreach (var pattern in new[] { "wedm_*.py", "wedm_rcu_*.properties", "wedm_*.env", "boot.properties" })
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(tempDirectory, pattern, SearchOption.TopDirectoryOnly))
                        paths.Add(f);
                }
                catch { /* best effort */ }
            }
        }

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            SecureDeleteFile(path);
    }

    public static void Zeroize(char[]? buffer)
    {
        if (buffer is null || buffer.Length == 0) return;
        Array.Fill(buffer, '\0');
    }

    public static void SecureDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var len = new FileInfo(path).Length;
            if (len > 0)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                fs.SetLength(len);
                var zeros = new byte[Math.Min(len, 65536)];
                for (long written = 0; written < len; written += zeros.Length)
                    fs.Write(zeros, 0, (int)Math.Min(zeros.Length, len - written));
            }
            File.Delete(path);
        }
        catch
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
