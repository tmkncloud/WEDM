using System.Text.RegularExpressions;

namespace WEDM.Engine.Transformation;

/// <summary>Write-only, workspace-scoped I/O with secret masking. Never modifies source environment paths.</summary>
internal static class TransformationSafeIO
{
    private static readonly Regex[] SecretPatterns =
    [
        new(@"(password\s*=\s*)([^\s\r\n;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(<password[^>]*>)([^<]+)(</password>)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(jdbc\.[^=]*password\s*=\s*)([^\s\r\n;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(-----BEGIN\s+(?:RSA\s+)?PRIVATE\s+KEY-----)([\s\S]*?)(-----END)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static string MaskSecrets(string content)
    {
        var masked = content;
        foreach (var pattern in SecretPatterns)
        {
            masked = pattern.IsMatch(masked)
                ? pattern.Replace(masked, m =>
                {
                    if (m.Groups.Count >= 3 && m.Groups[2].Success)
                        return m.Groups[1].Value + "***MASKED***" + (m.Groups.Count > 3 ? m.Groups[3].Value : "");
                    return "***MASKED***";
                })
                : masked;
        }
        return masked;
    }

    public static async Task WriteWorkspaceFileAsync(string workspaceRoot, string relativePath, string content, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
        if (!full.StartsWith(Path.GetFullPath(workspaceRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Workspace path traversal rejected.");

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, MaskSecrets(content), ct);
    }

    public static string ReadSourceFileSafe(string? sourcePath, int maxBytes = 512_000)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return string.Empty;

        var info = new FileInfo(sourcePath);
        if (info.Length > maxBytes)
            return File.ReadAllText(sourcePath)[..maxBytes] + "\n... [truncated]";

        return File.ReadAllText(sourcePath);
    }
}
