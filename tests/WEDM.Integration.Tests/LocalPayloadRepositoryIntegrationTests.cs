using FluentAssertions;
using Moq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Payload;
using Xunit;

namespace Orchestration.Integration.Tests;

public sealed class LocalPayloadRepositoryIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly LocalPayloadLocator _locator;

    public LocalPayloadRepositoryIntegrationTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "wedm-local-repo-it", Guid.NewGuid().ToString("N"));
        _locator = new LocalPayloadLocator(new Mock<ILoggingService>().Object);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Full_12c_layout_resolves_all_core_installers()
    {
        Seed12cLayout(_root);

        var config = new DeploymentConfiguration
        {
            WebLogicVersion = WebLogicVersion.WLS_12c,
            PayloadBasePath = _root,
            Components      = InstallationComponents.JDK
                | InstallationComponents.VCRedist
                | InstallationComponents.Infrastructure
                | InstallationComponents.FormsReports,
            ConfigureFormsReports = true,
            Domain = new DomainConfiguration
            {
                FormsReports = new FormsReportsConfiguration { InstallOhs = true, InstallWebUtil = true }
            },
            PayloadAcquisition = new PayloadAcquisitionConfiguration
            {
                UseLocalRepositoryOnly = true,
                ValidateChecksums      = false
            }
        };

        var report = await _locator.ValidateAndResolveAsync(config);

        report.CanProceed.Should().BeTrue();
        config.JdkInstallerPath.Should().NotBeNullOrWhiteSpace();
        config.InfrastructureInstallerPath.Should().Contain("infrastructure");
        config.FormsInstallerPath.Should().Contain("fr");
        config.WebTierInstallerPath.Should().Contain("ohs");
        config.WebUtilRootPath.Should().EndWith("webutil");
    }

    [Fact]
    public async Task Version_15c_uses_15c_folder()
    {
        var dir = Path.Combine(_root, "15c", "weblogic");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "fmw_15_wls.jar"), "x");

        var config = new DeploymentConfiguration
        {
            WebLogicVersion = WebLogicVersion.WLS_15c,
            PayloadBasePath = _root,
            Components      = InstallationComponents.WebLogicServer,
            PayloadAcquisition = new PayloadAcquisitionConfiguration { UseLocalRepositoryOnly = true }
        };

        var report = await _locator.ValidateAndResolveAsync(config);
        report.VersionFolder.Should().EndWith("15c");
        report.CanProceed.Should().BeTrue();
    }

    private static void Seed12cLayout(string root)
    {
        Write(Path.Combine(root, "12c", "jdk", "jdk-8u202-windows-x64.exe"));
        Write(Path.Combine(root, "12c", "vc", "vc_redist.x64.exe"));
        Write(Path.Combine(root, "12c", "infrastructure", "fmw_12.2.1.4.0_infrastructure.jar"));
        Write(Path.Combine(root, "12c", "forms", "setup_fmw_12.2.1.4.0_fr_win64.exe"));
        Write(Path.Combine(root, "12c", "webtier", "setup_fmw_12.2.1.4.0_ohs_win64.exe"));
        Directory.CreateDirectory(Path.Combine(root, "12c", "webutil", "java"));
        Directory.CreateDirectory(Path.Combine(root, "12c", "webutil", "win32"));
        Directory.CreateDirectory(Path.Combine(root, "12c", "webutil", "win64"));
    }

    private static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test-payload");
    }
}
