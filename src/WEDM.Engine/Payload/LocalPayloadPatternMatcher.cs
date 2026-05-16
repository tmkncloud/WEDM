namespace WEDM.Engine.Payload;

/// <summary>Wildcard file matching for local repository payloads (*.jar, *wls*.jar, etc.).</summary>
public static class LocalPayloadPatternMatcher
{
    public static IReadOnlyList<string> FindMatches(string directory, IReadOnlyList<string> patterns, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        if (!Directory.Exists(directory) || patterns.Count == 0)
            return [];

        var files = Directory.EnumerateFiles(directory, "*", searchOption).ToList();
        var matches = new List<string>();

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (IsMatch(name, pattern) && !matches.Contains(file, StringComparer.OrdinalIgnoreCase))
                    matches.Add(file);
            }
        }

        return matches
            .OrderByDescending(f => new FileInfo(f).Length)
            .ToList();
    }

    public static string? FindBestMatch(string directory, IReadOnlyList<string> patterns, SearchOption searchOption = SearchOption.TopDirectoryOnly)
        => FindMatches(directory, patterns, searchOption).FirstOrDefault();

    public static bool IsMatch(string fileName, string pattern)
    {
        if (string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase))
            return true;

        if (pattern.Contains('*', StringComparison.Ordinal))
            return WildcardMatch(fileName, pattern);

        return fileName.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            text, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
