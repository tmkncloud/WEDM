using FluentAssertions;
using WEDM.Domain.Models;
using WEDM.Engine.Jdk;
using WEDM.Engine.Jdk.Strategies;
using Xunit;

namespace WEDM.Engine.Tests.Jdk;

public sealed class JdkInstallerStrategyTests
{
    [Fact]
    public void OracleJdk8_strategy_builds_required_silent_properties()
    {
        var strategy = new OracleJdk8ExeInstallerStrategy();
        var config   = new DeploymentConfiguration
        {
            Java = new JavaConfiguration { JdkVersion = "1.8.0_202", InstallDirectory = @"C:\Program Files\Java" },
            Paths = new PathConfiguration { TempDirectory = @"C:\Oracle\Temp" }
        };

        strategy.CanHandle(@"D:\WEDM\12c\jdk\jdk-8u202-windows-x64.exe").Should().BeTrue();

        var inv = strategy.BuildInvocation(config, @"D:\WEDM\12c\jdk\jdk-8u202-windows-x64.exe");

        inv.TargetJavaHome.Should().Be(@"C:\Program Files\Java\jdk1.8.0_202");
        inv.Arguments.Should().Contain("/s");
        inv.Arguments.Should().Contain("INSTALL_SILENT=Enable");
        inv.Arguments.Should().Contain("AUTO_UPDATE=Disable");
        inv.Arguments.Should().ContainMatch("INSTALLDIR=*jdk1.8.0_202*");
        inv.Arguments.Should().Contain(@"INSTALLDIR=""C:\Program Files\Java\jdk1.8.0_202""");
    }

    [Fact]
    public void Temurin_strategy_uses_msiexec_with_feature_properties()
    {
        var strategy = new TemurinMsiInstallerStrategy();
        var config   = new DeploymentConfiguration
        {
            Java  = new JavaConfiguration { JdkVersion = "21.0.4", InstallDirectory = @"C:\Program Files\Eclipse Adoptium" },
            Paths = new PathConfiguration { TempDirectory = @"C:\Oracle\Temp" }
        };

        strategy.CanHandle(@"D:\payload\OpenJDK21U-jdk_x64_windows_hotspot_21.0.4_7.msi").Should().BeTrue();

        var inv = strategy.BuildInvocation(config, @"D:\payload\OpenJDK21U-jdk_x64_windows_hotspot_21.0.4_7.msi");

        inv.StrategyName.Should().Be("TemurinMsi");
        inv.ProcessPath.Should().EndWith("msiexec.exe");
        inv.Arguments.Should().Contain("/qn");
        inv.Arguments.Should().Contain("ADDLOCAL=FeatureMain,FeatureEnvironment,FeatureJarFileRunWith,FeatureJavaHome");
    }

    [Fact]
    public void Msi_strategy_uses_msiexec_with_qn()
    {
        var strategy = new MsiJdkInstallerStrategy();
        var config   = new DeploymentConfiguration
        {
            Java  = new JavaConfiguration { JdkVersion = "21.0.4" },
            Paths = new PathConfiguration { TempDirectory = @"C:\Oracle\Temp" }
        };

        var inv = strategy.BuildInvocation(config, @"D:\payload\OpenJDK21.msi");

        inv.ProcessPath.Should().EndWith("msiexec.exe");
        inv.Arguments.Should().Contain("/qn");
        inv.Arguments.Should().Contain("/norestart");
    }

    [Fact]
    public void Factory_selects_oracle_before_generic_exe()
    {
        var factory = new JdkInstallerStrategyFactory();
        var s = factory.Resolve(@"D:\WEDM\12c\jdk\jdk-8u202-windows-x64.exe");
        s.StrategyName.Should().Be("OracleJdk8Exe");
    }

    [Fact]
    public void ExitCodeNormalizer_maps_minus_80_to_invalid_arguments()
    {
        var r = JdkExitCodeNormalizer.Normalize(-80);
        r.Status.Should().Be(JdkInstallNormalizedStatus.InvalidArguments);
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void ExitCodeNormalizer_maps_3010_to_reboot_success()
    {
        var r = JdkExitCodeNormalizer.Normalize(3010);
        r.Status.Should().Be(JdkInstallNormalizedStatus.SuccessRebootRequired);
        r.Success.Should().BeTrue();
    }

    [Fact]
    public void TargetPathResolver_maps_1_8_0_202_to_jdk_folder()
    {
        var home = JdkTargetPathResolver.ResolveTargetJavaHome(new DeploymentConfiguration
        {
            Java = new JavaConfiguration { JdkVersion = "1.8.0_202", InstallDirectory = @"C:\Program Files\Java" }
        });
        home.Should().Be(@"C:\Program Files\Java\jdk1.8.0_202");
    }

    [Fact]
    public void BuildElevatedInstallScript_includes_argument_array()
    {
        var inv = new JdkInstallInvocation
        {
            ProcessPath = @"D:\WEDM\12c\jdk\jdk-8u202-windows-x64.exe",
            Arguments   = ["/s", "INSTALL_SILENT=Enable", @"INSTALLDIR=""C:\Program Files\Java\jdk1.8.0_202"""]
        };
        var script = JdkInstallationService.BuildElevatedInstallScript(inv);
        script.Should().Contain("$argList = @(");
        script.Should().Contain("INSTALL_SILENT=Enable");
        script.Should().Contain("RedirectStandardOutput");
    }
}
