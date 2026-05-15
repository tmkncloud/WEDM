using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Migration;
using Xunit;

namespace WEDM.Engine.Tests.Migration;

public sealed class MigrationReadinessScorerTests
{
    [Fact]
    public void Score_WithBlocker_SetsBlockedLevel()
    {
        var config = new MigrationConfiguration
        {
            Source = new MigrationEnvironmentProfile { Release = MiddlewareReleaseKind.Forms6i },
            Target = new MigrationEnvironmentProfile { Release = MiddlewareReleaseKind.Forms12c },
            FormsMetadata = new FormsReportsMetadataSnapshot { UsesOracleGraphics = true },
            Topology = new MiddlewareTopologySnapshot { DomainName = "TEST", ManagedServerCount = 2 },
        };

        var findings = new List<CompatibilityFinding>
        {
            new()
            {
                Severity = CompatibilitySeverity.Critical,
                BlocksMigration = true,
                Title = "Blocker",
            },
        };

        var snapshot = MigrationReadinessScorer.Score(config, findings);
        Assert.Equal(MigrationReadinessLevel.Blocked, snapshot.Level);
        Assert.Equal(1, snapshot.BlockerCount);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.ExecutiveSummary));
    }
}
