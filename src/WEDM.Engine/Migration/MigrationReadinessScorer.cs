using WEDM.Domain.Enums;
using WEDM.Domain.Migration;
using WEDM.Domain.Models;

namespace WEDM.Engine.Migration;

/// <summary>Weighted enterprise readiness scoring for migration compatibility assessments.</summary>
public static class MigrationReadinessScorer
{
    private static readonly Dictionary<CompatibilitySeverity, double> SeverityWeights = new()
    {
        [CompatibilitySeverity.Critical]      = 40,
        [CompatibilitySeverity.High]          = 22,
        [CompatibilitySeverity.Medium]        = 12,
        [CompatibilitySeverity.Low]           = 5,
        [CompatibilitySeverity.Informational] = 1,
    };

    public static MigrationReadinessSnapshot Score(
        MigrationConfiguration config,
        IReadOnlyList<CompatibilityFinding> findings)
    {
        var critical = findings.Count(f => f.Severity == CompatibilitySeverity.Critical);
        var high     = findings.Count(f => f.Severity == CompatibilitySeverity.High);
        var medium   = findings.Count(f => f.Severity == CompatibilitySeverity.Medium);
        var low      = findings.Count(f => f.Severity == CompatibilitySeverity.Low);
        var info     = findings.Count(f => f.Severity == CompatibilitySeverity.Informational);
        var blockers = findings.Count(f => f.BlocksMigration);

        var weightedRisk = findings.Sum(f => SeverityWeights.GetValueOrDefault(f.Severity, 0));
        var topologyRisk = ComputeTopologyRisk(config);
        weightedRisk += topologyRisk;

        var readinessPercent = Math.Clamp(100.0 - weightedRisk * 0.55, 0, 100);
        var score = (int)Math.Round(readinessPercent);

        var complexity = DetermineComplexity(config, blockers, critical, high, medium);
        var effort     = DetermineEffort(config, complexity);
        var level      = ScoreToLevel(score, blockers);

        var path = MigrationVersionMatrix.DescribeUpgradePath(config.Source.Release, config.Target.Release);

        return new MigrationReadinessSnapshot
        {
            Score             = score,
            ReadinessPercent  = Math.Round(readinessPercent, 1),
            WeightedRiskScore = Math.Round(weightedRisk, 1),
            Level             = level,
            Complexity        = complexity,
            EffortCategory    = effort,
            BlockerCount      = blockers,
            CriticalCount     = critical,
            HighCount         = high,
            MediumCount       = medium,
            LowCount          = low,
            WarningCount      = high + medium,
            Summary           = BuildSummary(level, path),
            ExecutiveSummary  = BuildExecutiveSummary(config, path, readinessPercent, complexity, blockers, effort),
            TechnicalSummary  = BuildTechnicalSummary(config, findings, critical, high, medium),
            AssessedAtUtc     = DateTimeOffset.UtcNow,
        };
    }

    private static double ComputeTopologyRisk(MigrationConfiguration config)
    {
        var risk = 0.0;
        if (!config.Topology.NodeManagerConfigured) risk += 8;
        if (!config.Topology.SslEnabled) risk += 6;
        if (config.FormsMetadata.UsesWebUtil) risk += 10;
        if (config.Topology.JvmArguments.Any(a => a.Contains("PermGen", StringComparison.OrdinalIgnoreCase))) risk += 12;
        risk += Math.Min(config.FormsMetadata.CustomPlsqlLibraries * 0.4, 15);
        risk += Math.Min(config.Topology.ManagedServerCount * 0.5, 12);
        risk += Math.Min(config.Topology.ClusterCount * 2.0, 8);
        if (!config.OracleInventory.InventoryHealthy) risk += 5;
        if (config.OracleInventory.Patches.Count > 40) risk += 6;
        if (!config.DiscoveryUsedRealScan) risk += 10;
        return risk;
    }

    private static MigrationComplexityKind DetermineComplexity(
        MigrationConfiguration config,
        int blockers,
        int critical,
        int high,
        int medium)
    {
        if (blockers > 0 || critical >= 2) return MigrationComplexityKind.Critical;
        if (critical >= 1 || high >= 3) return MigrationComplexityKind.High;
        if (high >= 1 || medium >= 4 || config.FormsMetadata.FormCount > 400) return MigrationComplexityKind.Medium;
        return MigrationComplexityKind.Low;
    }

    private static MigrationEffortCategory DetermineEffort(MigrationConfiguration config, MigrationComplexityKind complexity)
        => complexity switch
        {
            MigrationComplexityKind.Low      => MigrationEffortCategory.Short,
            MigrationComplexityKind.Medium   => MigrationEffortCategory.Standard,
            MigrationComplexityKind.High   => MigrationEffortCategory.Extended,
            MigrationComplexityKind.Critical => MigrationEffortCategory.Enterprise,
            _ => MigrationEffortCategory.Standard,
        };

    private static MigrationReadinessLevel ScoreToLevel(int score, int blockers)
    {
        if (blockers > 0) return MigrationReadinessLevel.Blocked;
        return score switch
        {
            >= 85 => MigrationReadinessLevel.Ready,
            >= 70 => MigrationReadinessLevel.ReadyWithRemediation,
            >= 50 => MigrationReadinessLevel.ModerateRisk,
            _     => MigrationReadinessLevel.HighRisk,
        };
    }

    private static string BuildSummary(MigrationReadinessLevel level, string path) => level switch
    {
        MigrationReadinessLevel.Blocked =>
            "Migration readiness is blocked — resolve critical compatibility items before cutover planning.",
        MigrationReadinessLevel.HighRisk =>
            $"Elevated risk on upgrade path {path}. Schedule extended remediation and validation cycles.",
        MigrationReadinessLevel.ModerateRisk =>
            "Moderate modernization risk — proceed with phased migration and validation gates.",
        MigrationReadinessLevel.ReadyWithRemediation =>
            "Ready with remediation — no blockers; address warnings during staged migration waves.",
        MigrationReadinessLevel.Ready =>
            "Strong migration readiness — recommended to finalize strategy and operational runbook.",
        _ => "Compatibility analysis pending.",
    };

    private static string BuildExecutiveSummary(
        MigrationConfiguration config,
        string path,
        double readinessPercent,
        MigrationComplexityKind complexity,
        int blockers,
        MigrationEffortCategory effort)
    {
        return $"Executive assessment for {config.Name}: upgrade path {path} shows {readinessPercent:F1}% migration readiness. " +
               $"Modernization complexity is {complexity} with estimated effort category {effort}. " +
               (blockers > 0
                   ? $"{blockers} blocking item(s) require executive sign-off before scheduling production cutover."
                   : "No blocking items detected — proceed to remediation planning and operational readiness review.");
    }

    private static string BuildTechnicalSummary(
        MigrationConfiguration config,
        IReadOnlyList<CompatibilityFinding> findings,
        int critical,
        int high,
        int medium)
    {
        return $"Technical assessment across {findings.Count} compatibility items " +
               $"({critical} critical, {high} high, {medium} medium). " +
               $"Source domain {config.Topology.DomainName ?? "n/a"} hosts {config.Topology.ManagedServerCount} managed servers " +
               $"and {config.FormsMetadata.FormCount} Forms modules. " +
               "Review JVM configuration, authentication integration, and Reports runtime dependencies in the detailed findings register.";
    }
}
