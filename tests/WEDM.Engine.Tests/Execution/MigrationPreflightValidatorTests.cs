using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Execution;
using Xunit;

namespace WEDM.Engine.Tests.Execution;

public sealed class MigrationPreflightValidatorTests
{
    [Fact]
    public void Validate_FailsWhenTransformationNotComplete()
    {
        var validator = new MigrationPreflightValidator();
        var config = new MigrationConfiguration { TransformationCompleted = false };
        var result = validator.Validate(config, new MigrationExecutionOptions());

        Assert.False(result.Passed);
        Assert.Contains(result.Checks, c => c.Severity == PreflightSeverity.Blocker);
    }
}
