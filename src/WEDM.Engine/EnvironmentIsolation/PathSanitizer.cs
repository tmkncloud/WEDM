using System.Text;
using WEDM.Domain.Models;

namespace WEDM.Engine.EnvironmentIsolation;

/// <summary>
/// Builds and analyses sanitized PATH strings for Oracle tool invocations.
///
/// Design rules:
///   1. Always preserve Windows system paths (System32, SysWOW64, WindowsPowerShell, etc.)
///   2. Always deduplicate — first occurrence wins; case-insensitive comparison on Windows.
///   3. Remove all stale Oracle / JDK entries from the machine PATH.
///   4. Add tool-specific required paths at the front (JAVA_HOME\bin, OPatch, etc.)
///   5. Normalize directory separators to backslash; strip trailing separators.
///   6. Never mutate the machine or user PATH — all output is for injection only.
/// </summary>
public static class PathSanitizer
{
    // ─────────────────────────────────────────────────────────────────────────
    // Windows system PATH segments that must always be preserved
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly string[] WindowsSystemPatterns =
    [
        @"windows\system32",
        @"windows\syswow64",
        @"windows\system32\wbem",
        @"windows\system32\windowspowershell",
        @"windows\system32\openssh",
        @"windows\",            // catch-all for any Windows sub-dir
        @"program files\windowsapps",
        @"\windows",
    ];

    // ─────────────────────────────────────────────────────────────────────────
    // Known stale Oracle / JDK path patterns to strip
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly string[] StaleOraclePatterns =
    [
        "oracle",
        "weblogic",
        "wlserver",
        "middleware",
        "oracle_mw",
        "fmw",
        "opatch",
        "wlst",
        "mwhome",
        "oraclehome",
        "forms",
        "ohs",
        "jdk1.",
        "jre1.",
        "jdk-",
        "jre-",
        "java\\jdk",
        "java\\jre",
        "java\\bin",
    ];

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a sanitized PATH string suitable for injection into Oracle tool invocations.
    ///
    /// Construction order:
    ///   1. <paramref name="prependPaths"/> (tool-specific, e.g. JAVA_HOME\bin, OPatch dir)
    ///   2. Windows system paths from the current machine PATH
    ///   3. Any non-Oracle, non-stale paths from the machine PATH (optional, see <paramref name="includeNonOracle"/>)
    ///
    /// Stale Oracle/JDK entries from the machine PATH are always excluded.
    /// Duplicates are removed; first occurrence wins (case-insensitive on Windows).
    /// </summary>
    /// <param name="machinePath">The raw machine or process PATH to sanitize.</param>
    /// <param name="prependPaths">Tool-specific paths to place at the front (highest priority).</param>
    /// <param name="includeNonOracle">
    ///   When true, non-Oracle non-system paths from the machine PATH are preserved after system paths.
    ///   When false, only system paths + prepend paths are included (maximum isolation).
    /// </param>
    public static string Build(
        string machinePath,
        IEnumerable<string>? prependPaths = null,
        bool includeNonOracle = true)
    {
        var segments = new List<string>();
        var seen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Tool-specific prepend paths (highest priority)
        foreach (var p in prependPaths ?? [])
        {
            var normalized = NormalizePath(p);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            if (seen.Add(normalized))
                segments.Add(normalized);
        }

        var machineSegments = Split(machinePath);

        // 2. Windows system paths (always preserved)
        foreach (var seg in machineSegments)
        {
            var normalized = NormalizePath(seg);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            if (!IsWindowsSystemPath(normalized)) continue;
            if (seen.Add(normalized))
                segments.Add(normalized);
        }

        // 3. Non-Oracle paths (when requested)
        if (includeNonOracle)
        {
            foreach (var seg in machineSegments)
            {
                var normalized = NormalizePath(seg);
                if (string.IsNullOrWhiteSpace(normalized)) continue;
                if (IsWindowsSystemPath(normalized)) continue; // already added
                if (IsStaleOraclePath(normalized)) continue;   // always excluded
                if (seen.Add(normalized))
                    segments.Add(normalized);
            }
        }

        return string.Join(';', segments);
    }

    /// <summary>
    /// Analyses a raw PATH string and classifies each segment.
    /// Returns a <see cref="PathAnalysisResult"/> with stale, duplicate, Oracle, Java, system segment lists.
    /// </summary>
    /// <param name="rawPath">The PATH string to analyse.</param>
    /// <param name="requiredPaths">Paths that must be present; reported in <see cref="PathAnalysisResult.MissingRequired"/>.</param>
    public static PathAnalysisResult Analyse(string rawPath, IEnumerable<string>? requiredPaths = null)
    {
        var segments = Split(rawPath).Select(NormalizePath).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<string>();
        var oracle     = new List<string>();
        var java       = new List<string>();
        var system     = new List<string>();
        var stale      = new List<string>();

        foreach (var seg in segments)
        {
            if (!seen.Add(seg))
            {
                duplicates.Add(seg);
                continue;
            }

            if (IsWindowsSystemPath(seg))
                system.Add(seg);
            else if (IsStaleOraclePath(seg))
                stale.Add(seg);
            else if (IsJavaPath(seg))
                java.Add(seg);
            else if (IsOraclePath(seg))
                oracle.Add(seg);
        }

        var required = (requiredPaths ?? []).Select(NormalizePath).ToList();
        var missing  = required.Where(r => !seen.Contains(r)).ToList();

        return new PathAnalysisResult
        {
            AllSegments      = segments.AsReadOnly(),
            OracleSegments   = oracle.AsReadOnly(),
            JavaSegments     = java.AsReadOnly(),
            SystemSegments   = system.AsReadOnly(),
            DuplicateSegments = duplicates.AsReadOnly(),
            StaleSegments    = stale.AsReadOnly(),
            MissingRequired  = missing.AsReadOnly(),
        };
    }

    /// <summary>
    /// Produces a human-readable diff between two PATH strings.
    /// Returns a list of "(+) added" and "(-) removed" lines.
    /// </summary>
    public static IReadOnlyList<string> Diff(string baselinePath, string currentPath)
    {
        var baseline = Split(baselinePath).Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current  = Split(currentPath).Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<string>();

        foreach (var seg in current.Except(baseline, StringComparer.OrdinalIgnoreCase))
            result.Add($"(+) {seg}");

        foreach (var seg in baseline.Except(current, StringComparer.OrdinalIgnoreCase))
            result.Add($"(-) {seg}");

        return result.AsReadOnly();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Splits a PATH string on semicolons, trimming empty entries.</summary>
    public static IReadOnlyList<string> Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        return path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Normalize path separators to backslash and strip trailing separator.</summary>
    public static string NormalizePath(string path)
        => path.Replace('/', '\\').TrimEnd('\\', '/').Trim();

    /// <summary>True when the path segment belongs to a Windows system directory.</summary>
    public static bool IsWindowsSystemPath(string normalizedPath)
    {
        var lower = normalizedPath.ToLowerInvariant();
        foreach (var pattern in WindowsSystemPatterns)
            if (lower.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True when the path segment is a known stale Oracle/JDK entry that must be excluded.</summary>
    public static bool IsStaleOraclePath(string normalizedPath)
    {
        var lower = normalizedPath.ToLowerInvariant();
        foreach (var pattern in StaleOraclePatterns)
            if (lower.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True when the path segment appears to be a Java-related directory.</summary>
    public static bool IsJavaPath(string normalizedPath)
    {
        var lower = normalizedPath.ToLowerInvariant();
        return lower.Contains("jdk") || lower.Contains("jre") || lower.Contains("java");
    }

    /// <summary>True when the path segment appears to be an Oracle middleware directory.</summary>
    public static bool IsOraclePath(string normalizedPath)
    {
        var lower = normalizedPath.ToLowerInvariant();
        return lower.Contains("oracle") || lower.Contains("weblogic") || lower.Contains("wlserver")
            || lower.Contains("fmw") || lower.Contains("middleware");
    }

    /// <summary>
    /// Builds the PowerShell fragment that sets $env:PATH from an already-sanitized PATH string.
    /// Escapes single-quotes in each segment.
    /// </summary>
    public static string BuildPathPreambleLine(string sanitizedPath)
    {
        // Escape single-quotes for PowerShell string literal
        var escaped = sanitizedPath.Replace("'", "''", StringComparison.Ordinal);
        return $"$env:PATH = '{escaped}'";
    }
}
