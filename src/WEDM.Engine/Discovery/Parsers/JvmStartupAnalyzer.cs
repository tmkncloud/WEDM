using System.Text.RegularExpressions;
using WEDM.Domain.Models;

namespace WEDM.Engine.Discovery.Parsers;

public static class JvmStartupAnalyzer
{
    private static readonly Regex ArgPattern = new(
        @"-(?:Xmx|Xms|XX:[\w:]+|D[\w\.]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] DeprecatedTokens =
    [
        "PermSize", "MaxPermSize", "UseConcMarkSweepGC", "CMSPermGenSweepingEnabled",
    ];

    public static List<string> ExtractJvmArguments(string domainHome, string? adminServerName = null)
    {
        var args = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        adminServerName ??= "AdminServer";

        var scriptCandidates = new[]
        {
            Path.Combine(domainHome, "bin", "setDomainEnv.cmd"),
            Path.Combine(domainHome, "bin", "setDomainEnv.sh"),
            Path.Combine(domainHome, "servers", adminServerName, "bin", "setStartupEnv.cmd"),
        };

        foreach (var script in scriptCandidates.Where(File.Exists))
        {
            try
            {
                var text = File.ReadAllText(script);
                foreach (Match m in ArgPattern.Matches(text))
                    args.Add(m.Value);
            }
            catch { }
        }

        return args.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<EnvironmentDiscoveryFinding> AnalyzeDeprecatedArgs(IReadOnlyList<string> jvmArgs)
    {
        var findings = new List<EnvironmentDiscoveryFinding>();
        foreach (var token in DeprecatedTokens)
        {
            if (jvmArgs.Any(a => a.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(new EnvironmentDiscoveryFinding
                {
                    Category = Domain.Enums.CompatibilityRiskCategory.JvmConfiguration,
                    Title    = $"Deprecated JVM flag: {token}",
                    Detail   = $"Startup scripts reference '{token}' which is incompatible with modern JDK tiers.",
                    Severity = Domain.Enums.CompatibilitySeverity.High,
                });
            }
        }

        return findings;
    }
}
