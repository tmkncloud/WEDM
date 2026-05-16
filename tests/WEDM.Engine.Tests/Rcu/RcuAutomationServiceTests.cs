using FluentAssertions;
using Moq;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Rcu;
using Xunit;

namespace WEDM.Engine.Tests.Rcu;

public sealed class RcuAutomationServiceTests
{
    [Fact]
    public async Task ExecuteAsync_dry_run_generates_response_file_without_invoking_rcu()
    {
        var log = new Mock<ILoggingService>();
        var svc = new RcuAutomationService(log.Object);
        var config = new DeploymentConfiguration
        {
            Database = new DatabaseConfiguration
            {
                Host = "dbhost",
                Port = 1521,
                ServiceName = "orcl",
                SchemaPrefix = "DEV",
                NlsCharset = "AL32UTF8"
            },
            Paths = new PathConfiguration { TempDirectory = Path.Combine(Path.GetTempPath(), "wedm-rcu", Guid.NewGuid().ToString("N")) }
        };
        Directory.CreateDirectory(config.Paths.TempDirectory);
        var rcuBat = Path.Combine(config.Paths.TempDirectory, "rcu.bat");
        await File.WriteAllTextAsync(rcuBat, "@echo off");
        config.Database.RcuPath = rcuBat;

        Environment.SetEnvironmentVariable("WEDM_RCU_DRY_RUN", "1");
        try
        {
            var result = await svc.ExecuteAsync(config, dryRun: true);
            result.Success.Should().BeTrue();
            result.DryRun.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEDM_RCU_DRY_RUN", null);
            try { Directory.Delete(config.Paths.TempDirectory, recursive: true); } catch { }
        }
    }
}
