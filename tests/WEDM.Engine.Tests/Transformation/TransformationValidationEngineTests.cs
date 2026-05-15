using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Transformation;
using Xunit;

namespace WEDM.Engine.Tests.Transformation;

public sealed class TransformationValidationEngineTests
{
    [Fact]
    public void Validate_FlagsMissingDiscovery()
    {
        var engine = new TransformationValidationEngine();
        var config = new MigrationConfiguration { DiscoveryCompleted = false, AssessmentCompleted = true };
        var workspace = Path.Combine(Path.GetTempPath(), "wedm-val-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "wlst"));
        var result = new TransformationExecutionResult
        {
            WorkspacePath = workspace,
            Artifacts = [new GeneratedTransformationArtifact { Kind = TransformationArtifactKind.WlstScript, RelativePath = "wlst/test.py" }],
        };

        File.WriteAllText(Path.Combine(workspace, "wlst", "test.py"), "exit()");

        var summary = engine.Validate(config, result);
        Assert.False(summary.Passed);
        Assert.Contains(summary.Messages, m => m.Severity == TransformationValidationSeverity.Blocker);
    }
}
