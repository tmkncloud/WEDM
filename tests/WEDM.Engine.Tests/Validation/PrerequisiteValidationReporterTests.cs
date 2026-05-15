using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Validation;
using Xunit;

namespace WEDM.Engine.Tests.Validation;

public sealed class PrerequisiteValidationReporterTests
{
    [Fact]
    public void FormatDetailedBlockers_includes_expected_actual_remediation()
    {
        var result = new PrerequisiteValidationResult();
        result.Fatal("JDKVersionValidation", "JDK not installed",
            "Install Temurin/OpenJDK 8", actual: "Not installed", expected: "JDK 8");

        var text = PrerequisiteValidationReporter.FormatDetailedBlockers(result);

        Assert.Contains("JDKVersionValidation", text);
        Assert.Contains("Expected: JDK 8", text);
        Assert.Contains("Actual: Not installed", text);
        Assert.Contains("Remediation: Install Temurin/OpenJDK 8", text);
    }

    [Fact]
    public void IsRetryRecommended_true_only_for_database_checks()
    {
        var dbOnly = new PrerequisiteValidationResult();
        dbOnly.Fail("Database.TCP", "timeout", actual: "x", expected: "y");
        Assert.True(PrerequisiteValidationReporter.IsRetryRecommended(dbOnly));

        var payload = new PrerequisiteValidationResult();
        payload.Fatal("Payload.JDK", "missing");
        Assert.False(PrerequisiteValidationReporter.IsRetryRecommended(payload));
    }
}
