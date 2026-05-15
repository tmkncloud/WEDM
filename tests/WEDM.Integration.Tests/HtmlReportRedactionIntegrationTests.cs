using WEDM.Domain.Models;
using WEDM.Domain.Enums;
using WEDM.Infrastructure.Logging;
using WEDM.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace Orchestration.Integration.Tests;

public sealed class HtmlReportRedactionIntegrationTests
{
    [Fact]
    public async Task WriteHtmlReportAsync_RedactsSecretsInPrerequisitesSection()
    {
        var leak = "password=mySecret123";
        var temp = Path.Combine(Path.GetTempPath(), $"wedm-integration-{Guid.NewGuid():N}.html");

        try
        {
            var log = new SerilogLoggingService(Path.Combine(Path.GetTempPath(), $"wedm-intlog-{Guid.NewGuid():N}"));
            log.BeginSession(Guid.NewGuid(), "integration");

            var validation = PrerequisiteValidationResult.New(Guid.NewGuid());
            validation.Fatal("Connectivity", leak, remediation: "Fix listener settings.");

            var report = new DeploymentReport
            {
                DeploymentName       = "R",
                MachineName       = Environment.MachineName,
                FinalStatus       = DeploymentStatus.Failed,
                Validation        = validation,
                Steps             =
                [
                    new DeploymentStep { Sequence = 1, Name = "ValidatePrerequisites", Category = "V", Description = "",
                        Status = StepStatus.Failed, ErrorMessage = leak, ExitCode = 10 }
                ]
            };

            await log.WriteHtmlReportAsync(report, temp);

            var html = await File.ReadAllTextAsync(temp);

            SecretRedactor.FindLeaks(html).Should().BeEmpty();
            html.Should().NotContain("mySecret123");
            html.Should().Contain("***REDACTED***");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* test cleanup */ }
    }
}
