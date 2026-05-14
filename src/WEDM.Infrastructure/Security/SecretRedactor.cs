using System.Text.RegularExpressions;

namespace WEDM.Infrastructure.Security;

/// <summary>Removes likely secret material from strings before logging or persisting diagnostics.</summary>
public static class SecretRedactor
{
    private static readonly Regex[] Patterns =
    {
        new(@"(?i)(password|pwd|passphrase|adminpass|trustpass|keystorepass|identitypass)\s*[=:]\s*\S+", RegexOptions.Compiled),
        new(@"(?i)(WEDM_ADMIN_PASS\s*=\s*)([^\r\n]+)", RegexOptions.Compiled),
        new(@"(?i)(BEGIN\s+PRIVATE\s+KEY.*?END\s+PRIVATE\s+KEY)", RegexOptions.Compiled | RegexOptions.Singleline),
        new(@"(?i)(BEGIN\s+RSA\s+PRIVATE\s+KEY.*?END\s+RSA\s+PRIVATE\s+KEY)", RegexOptions.Compiled | RegexOptions.Singleline),
    };

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var s = input;
        s = Patterns[0].Replace(s, m => $"{m.Groups[1].Value}=***REDACTED***");
        s = Patterns[1].Replace(s, m => $"{m.Groups[1].Value}***REDACTED***");
        s = Patterns[2].Replace(s, "***PRIVATE-KEY-REDACTED***");
        s = Patterns[3].Replace(s, "***PRIVATE-KEY-REDACTED***");
        return s;
    }
}
