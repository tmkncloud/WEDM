using System.Text.RegularExpressions;

namespace WEDM.Engine.Discovery;

/// <summary>Read-only file access with secret masking and safe enumeration.</summary>
public static class SafeDiscoveryIO
{
    private static readonly Regex SecretPattern = new(
        @"(password|passwd|pwd|secret|credential)\s*[=:]\s*[^\s;""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool DirectoryExists(string? path)
        => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    public static bool FileExists(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public static string? ReadAllTextSafe(string? path, int maxBytes = 2_000_000)
    {
        if (!FileExists(path)) return null;
        try
        {
            var info = new FileInfo(path!);
            if (info.Length > maxBytes) return null;

            var text = File.ReadAllText(path!);
            return MaskSecrets(text);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string MaskSecrets(string text)
        => SecretPattern.Replace(text, m =>
        {
            var eq = m.Value.IndexOf('=');
            var sep = eq >= 0 ? '=' : ':';
            var key = m.Value[..m.Value.IndexOf(sep)];
            return $"{key}{sep}***";
        });

    public static IEnumerable<string> EnumerateFilesSafe(string? root, string pattern, int maxFiles = 5000)
    {
        if (!DirectoryExists(root)) yield break;

        var count = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root!, pattern, SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (count++ >= maxFiles) yield break;
            yield return file;
        }
    }

    public static async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return await action(cts.Token);
    }
}
