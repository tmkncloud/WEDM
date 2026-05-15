using System.Text.RegularExpressions;

namespace WEDM.Infrastructure.Security;

/// <summary>
/// Removes likely secret material from strings before logging or persisting diagnostics.
///
/// Covered patterns:
///   • Key=value pairs for common password fields (case-insensitive)
///   • WEDM runtime environment variables (WEDM_ADMIN_PASS, etc.)
///   • Base64-encoded passwords injected into PowerShell bodies via FromBase64String(…)
///   • JDBC / ADO.NET connection-string password segments
///   • PEM private keys (RSA, EC, PKCS#8)
///   • Bearer tokens / Authorization headers
///   • Basic auth credentials in URLs  (https://user:pass@host)
/// </summary>
public static class SecretRedactor
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        // ── key=value password fields ──────────────────────────────────────────
        (new(@"(?i)(password|pwd|passphrase|adminpass|trustpass|keystorepass|identitypass|secretkey|apikey|api_key|token)\s*[=:]\s*\S+",
             RegexOptions.Compiled),
         "$1=***REDACTED***"),

        // ── WEDM runtime env-vars set in PowerShell bodies ─────────────────────
        (new(@"(?i)(WEDM_ADMIN_PASS\s*=\s*)([^\r\n]+)",
             RegexOptions.Compiled),
         "$1***REDACTED***"),

        (new(@"(?i)(WEDM_ADMIN_USER\s*=\s*)('[^']*')",
             RegexOptions.Compiled),
         "$1'***REDACTED***'"),

        // ── Base64-encoded passwords in FromBase64String('…') calls ────────────
        // Matches: FromBase64String('<base64 blob>')
        (new(@"(?i)(\[Convert\]::)?(FromBase64String\s*\(\s*')([\w+/=]{20,}?)(')",
             RegexOptions.Compiled),
         "$1$2***REDACTED***$4"),

        // ── JDBC / ODBC / ADO.NET connection-string password segment ───────────
        (new(@"(?i)(password\s*=\s*)([^;'""\s]+)",
             RegexOptions.Compiled),
         "$1***REDACTED***"),

        // ── Basic auth credentials in URLs  https://user:password@host ────────
        (new(@"(?i)(https?://)([^:@/\s]+):([^@/\s]+)@",
             RegexOptions.Compiled),
         "$1$2:***REDACTED***@"),

        // ── Bearer / Authorization header ────────────────────────────────────
        (new(@"(?i)(Authorization\s*:\s*(Bearer|Basic)\s+)(\S+)",
             RegexOptions.Compiled),
         "$1***REDACTED***"),

        // ── PEM private keys ─────────────────────────────────────────────────
        (new(@"(?i)(-----BEGIN\s+(?:RSA\s+|EC\s+|ENCRYPTED\s+)?PRIVATE\s+KEY-----.*?-----END\s+(?:RSA\s+|EC\s+|ENCRYPTED\s+)?PRIVATE\s+KEY-----)",
             RegexOptions.Compiled | RegexOptions.Singleline),
         "***PRIVATE-KEY-REDACTED***"),
    ];

    /// <summary>Apply all redaction rules and return a sanitised copy of <paramref name="input"/>.</summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var s = input;
        foreach (var (pattern, replacement) in Rules)
            s = pattern.Replace(s, replacement);
        return s;
    }

    /// <summary>
    /// Verify that <paramref name="text"/> contains no unredacted secrets.
    /// Returns a list of pattern names that matched — empty means clean.
    /// Intended for use in automated tests.
    /// </summary>
    public static IReadOnlyList<string> FindLeaks(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var leaks = new List<string>();
        // Quick heuristic checks — not a substitute for the full rule set.
        var checks = new (Regex Re, string Label)[]
        {
            (new(@"(?i)password\s*[=:]\s*(?![\*]+)[^\s;,""']{4,}", RegexOptions.Compiled), "password-field"),
            (new(@"(?i)WEDM_ADMIN_PASS\s*=\s*(?!\*{3})\S",         RegexOptions.Compiled), "wedm-admin-pass"),
            (new(@"(?i)(\[Convert\]::)?FromBase64String\s*\(\s*'[\w+/=]{20,}'",
                 RegexOptions.Compiled), "base64-password"),
            (new(@"-----BEGIN\s+(?:RSA\s+)?PRIVATE\s+KEY-----",        RegexOptions.Compiled), "pem-private-key"),
        };
        foreach (var (re, label) in checks)
            if (re.IsMatch(text)) leaks.Add(label);
        return leaks;
    }
}
