using System.Text.RegularExpressions;
using WEDM.Domain.Models;

namespace WEDM.Engine.ProcessLifecycle;

/// <summary>
/// Classifies Oracle-related processes by analyzing their command line, JVM arguments,
/// classpath entries, working directory, and middleware home references.
///
/// Classification confidence levels:
///   90–100 : Definitive — specific JVM property or launcher pattern matched.
///   60–89  : Probable   — multiple indirect markers; treat as the classified kind.
///   30–59  : Possible   — single weak marker; treat cautiously.
///   0–29   : Unknown    — insufficient evidence; MUST NOT terminate automatically.
///
/// Design invariant: the classifier never modifies process state. It is pure analysis.
/// </summary>
public static class OracleProcessClassifier
{
    // ─────────────────────────────────────────────────────────────────────────
    // Process name allowlist — only processes with these names are candidates
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> OracleProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "java", "javaw",          // JVM hosts — most Oracle tools are java processes
        "nodemanager",            // may appear as dedicated process on some platforms
        "ohs",  "httpd",         // Oracle HTTP Server
        "wlsvc",                  // WLS Windows service host
        "opatch",                 // OPatch binary (rare; usually invoked via java)
        "msiexec",                // JDK MSI installer
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Classification patterns — evaluated in priority order
    // ─────────────────────────────────────────────────────────────────────────

    // Definitive JVM property markers (-D properties in JVM args)
    private static readonly (string Pattern, OracleProcessKind Kind, int Confidence)[] JvmPropertyPatterns =
    [
        (@"-Dweblogic\.Name=AdminServer",       OracleProcessKind.AdminServer,   95),
        (@"-Dweblogic\.Name=",                  OracleProcessKind.ManagedServer, 90),
        (@"-Doracle\.nodemanager",               OracleProcessKind.NodeManager,   95),
        (@"weblogic\.NodeManager",               OracleProcessKind.NodeManager,   95),
        (@"-Djava\.class\.path=.*nodemanager",  OracleProcessKind.NodeManager,   85),
        (@"weblogic\.Server.*nodemanager",       OracleProcessKind.NodeManager,   90),
        (@"oracle\.opatch",                      OracleProcessKind.OPatch,        95),
        (@"-jar.*opatch",                        OracleProcessKind.OPatch,        90),
        (@"oracle\.install\.driver\.oui",        OracleProcessKind.OUI,           98),
        (@"-jar.*fmw_.*\.jar",                   OracleProcessKind.OUI,           95),
        (@"-jar.*wls.*\.jar",                    OracleProcessKind.OUI,           90),
        (@"-jar.*infrastructure.*\.jar",         OracleProcessKind.OUI,           90),
        (@"oracle\.forms",                       OracleProcessKind.FormsRuntime,  90),
        (@"oracle\.reports",                     OracleProcessKind.ReportsRuntime,90),
        (@"oracle\.tip\.adapter",                OracleProcessKind.FormsRuntime,  70),
        (@"weblogic\.WLST",                      OracleProcessKind.WLST,          95),
        (@"wlst\.cmd|wlst\.sh",                  OracleProcessKind.WLST,          90),
        (@"oracle\.rcu",                         OracleProcessKind.RCU,           95),
        (@"rcu\.bat",                            OracleProcessKind.RCU,           90),
    ];

    // Secondary classpath / working directory markers
    private static readonly (string Pattern, OracleProcessKind Kind, int Confidence)[] ClasspathPatterns =
    [
        (@"wlserver.server.lib",                 OracleProcessKind.AdminServer,   70),
        (@"nodemanager.jar",                     OracleProcessKind.NodeManager,   80),
        (@"OPatch.lib",                          OracleProcessKind.OPatch,        75),
        (@"forms.frcommon",                      OracleProcessKind.FormsRuntime,  75),
        (@"reports.lib",                         OracleProcessKind.ReportsRuntime,75),
    ];

    // Oracle home path patterns
    private static readonly Regex OracleHomePath = new(
        @"([A-Za-z]:\\[^"";\s]*(?:oracle|middleware|wlserver|fmw)[^"";\s]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Oracle process name patterns (for processes named other than java)
    private static readonly (string Name, OracleProcessKind Kind)[] NamePatterns =
    [
        ("nodemanager", OracleProcessKind.NodeManager),
        ("wlsvc",       OracleProcessKind.ManagedServer),
        ("opatch",      OracleProcessKind.OPatch),
        ("httpd",       OracleProcessKind.OHS),
        ("ohs",         OracleProcessKind.OHS),
    ];

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the process name is in the Oracle process candidate list.
    /// Call this first to filter non-Oracle processes before the more expensive classification.
    /// </summary>
    public static bool IsOracleCandidate(string processName)
        => OracleProcessNames.Contains(processName);

    /// <summary>
    /// Classifies an Oracle process candidate given its name, command line, and optional working directory.
    ///
    /// Returns a <see cref="ProcessClassificationResult"/> with kind, confidence, and extracted evidence.
    /// Always returns a result — never throws.
    /// </summary>
    public static ProcessClassificationResult Classify(
        string processName,
        string? commandLine,
        string? workingDirectory = null)
    {
        // 1. Fast path: named non-Java processes
        foreach (var (name, kind) in NamePatterns)
        {
            if (processName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return Result(kind, 90, $"Process name '{processName}' matches {kind}");
        }

        // 2. JVM property / argument patterns (highest confidence)
        if (!string.IsNullOrWhiteSpace(commandLine))
        {
            var extractedHomes = ExtractOracleHomePaths(commandLine);
            var extractedArgs  = ExtractJvmArgs(commandLine);

            foreach (var (pattern, kind, confidence) in JvmPropertyPatterns)
            {
                if (Regex.IsMatch(commandLine, pattern, RegexOptions.IgnoreCase))
                    return Result(kind, confidence,
                        $"Command line matched pattern '{pattern}'",
                        extractedHomes, extractedArgs);
            }

            // 3. Classpath / secondary patterns
            foreach (var (pattern, kind, confidence) in ClasspathPatterns)
            {
                if (Regex.IsMatch(commandLine, pattern, RegexOptions.IgnoreCase))
                    return Result(kind, confidence,
                        $"Classpath/secondary pattern '{pattern}' matched",
                        extractedHomes, extractedArgs);
            }

            // 4. Oracle home path in command line → OrphanJvm if not otherwise classified
            if (extractedHomes.Count > 0)
                return Result(OracleProcessKind.OrphanJvm, 50,
                    $"Oracle home path reference found in command line: {extractedHomes[0]}",
                    extractedHomes, extractedArgs);

            // 5. Generic Oracle keyword in command line
            var lower = commandLine.ToLowerInvariant();
            if (ContainsOracleKeyword(lower))
                return Result(OracleProcessKind.OrphanJvm, 35,
                    "Generic Oracle keyword detected in command line",
                    extractedHomes, extractedArgs);
        }

        // 6. Working directory heuristic
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var wdLower = workingDirectory.ToLowerInvariant();
            if (wdLower.Contains("oracle") || wdLower.Contains("middleware") || wdLower.Contains("wlserver"))
                return Result(OracleProcessKind.OrphanJvm, 25,
                    $"Working directory suggests Oracle: {workingDirectory}");
        }

        // 7. Cannot classify
        return Result(OracleProcessKind.Unknown, 0, "Insufficient evidence to classify");
    }

    /// <summary>
    /// Returns true when the given process should be considered an Oracle-related process
    /// based on confidence threshold.  Only processes with confidence ≥ 30 are acted on.
    /// </summary>
    public static bool IsConfidentlyOracle(ProcessClassificationResult result)
        => result.Kind != OracleProcessKind.Unknown && result.Confidence >= 30;

    // ─────────────────────────────────────────────────────────────────────────
    // Extraction helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Extracts Oracle middleware home paths from a command line string.</summary>
    public static IReadOnlyList<string> ExtractOracleHomePaths(string commandLine)
    {
        var matches = OracleHomePath.Matches(commandLine);
        return matches.Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
    }

    /// <summary>Extracts JVM -D property arguments from a command line.</summary>
    public static IReadOnlyList<string> ExtractJvmArgs(string commandLine)
    {
        // Match -Dproperty=value patterns
        var jvmArgPattern = new Regex(@"-D[\w\.\-]+=\S*", RegexOptions.Compiled);
        var matches = jvmArgPattern.Matches(commandLine);
        return matches.Select(m => m.Value).ToList().AsReadOnly();
    }

    /// <summary>
    /// Extracts the -Dweblogic.Name value from a command line, if present.
    /// Returns null when not found.
    /// </summary>
    public static string? ExtractServerName(string commandLine)
    {
        var m = Regex.Match(commandLine, @"-Dweblogic\.Name=(\S+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static bool ContainsOracleKeyword(string lower)
    {
        return lower.Contains("oracle") || lower.Contains("weblogic") || lower.Contains("nodemanager")
            || lower.Contains("opatch") || lower.Contains("wlserver") || lower.Contains("fmw")
            || lower.Contains("middleware") || lower.Contains("forms") || lower.Contains("reports");
    }

    private static ProcessClassificationResult Result(
        OracleProcessKind kind,
        int confidence,
        string reason,
        IReadOnlyList<string>? homes = null,
        IReadOnlyList<string>? args  = null)
        => new()
        {
            Kind                = kind,
            Confidence          = confidence,
            Reason              = reason,
            ExtractedOracleHomes = homes ?? [],
            ExtractedJvmArgs    = args  ?? [],
        };
}
