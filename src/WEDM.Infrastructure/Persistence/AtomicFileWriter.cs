namespace WEDM.Infrastructure.Persistence;

/// <summary>Corruption-safe file writes via temp file + atomic replace.</summary>
public static class AtomicFileWriter
{
    private static readonly Dictionary<string, SemaphoreSlim> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task WriteAllTextAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken = default)
    {
        var gate = GetLock(targetPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tempPath = targetPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
                if (File.Exists(targetPath))
                    File.Replace(tempPath, targetPath, destinationBackupFileName: targetPath + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, targetPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* best effort */ }
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static SemaphoreSlim GetLock(string path)
    {
        lock (PathLocks)
        {
            if (!PathLocks.TryGetValue(path, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                PathLocks[path] = sem;
            }
            return sem;
        }
    }

    public static async Task WriteAllBytesAsync(
        string targetPath,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = targetPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            if (File.Exists(targetPath))
                File.Replace(tempPath, targetPath, destinationBackupFileName: targetPath + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tempPath, targetPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
        }
    }
}
