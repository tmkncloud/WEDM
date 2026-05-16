using FluentAssertions;
using Moq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Payload;
using Xunit;

namespace WEDM.Engine.Tests.Payload;

public sealed class LocalPayloadLocatorTests : IDisposable
{
    private readonly string _root;
    private readonly LocalPayloadLocator _sut;

    public LocalPayloadLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wedm-payload-it", Guid.NewGuid().ToString("N"));
        _sut  = new LocalPayloadLocator(new Mock<ILoggingService>().Object);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ValidateAndResolve_finds_jar_via_wildcard_pattern()
    {
        var versionDir = Path.Combine(_root, "12c");
        var infraDir   = Path.Combine(versionDir, "infrastructure");
        Directory.CreateDirectory(infraDir);
        var jar = Path.Combine(infraDir, "fmw_12.2.1.4.0_infrastructure.jar");
        await File.WriteAllTextAsync(jar, "fake-jar-content-for-test");

        var config = MinimalConfig(WebLogicVersion.WLS_12c);
        config.PayloadBasePath = _root;
        config.Components = InstallationComponents.Infrastructure;

        var report = await _sut.ValidateAndResolveAsync(config);

        report.CanProceed.Should().BeTrue();
        config.InfrastructureInstallerPath.Should().Be(jar);
    }

    [Fact]
    public async Task ValidateAndResolve_missing_folder_fails_with_remediation()
    {
        var config = MinimalConfig(WebLogicVersion.WLS_12c);
        config.PayloadBasePath = _root;
        config.Components = InstallationComponents.JDK;

        var report = await _sut.ValidateAndResolveAsync(config);

        report.CanProceed.Should().BeFalse();
        report.Findings.Should().NotBeEmpty();
        report.Findings.Should().OnlyContain(f => f.Severity == ValidationSeverity.Fatal);
        report.Findings[0].Remediation.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ValidateAndResolve_checksum_mismatch_is_fatal()
    {
        var versionDir = Path.Combine(_root, "12c");
        var jdkDir     = Path.Combine(versionDir, "jdk");
        Directory.CreateDirectory(jdkDir);
        var exe = Path.Combine(jdkDir, "jdk-8u202-windows-x64.exe");
        await File.WriteAllTextAsync(exe, "jdk-payload");

        var manifest = """
        {
          "version": "12c",
          "payloads": {
            "jdk": { "file": "jdk-8u202-windows-x64.exe", "sha256": "deadbeef" }
          }
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(versionDir, LocalPayloadManifestReader.ManifestFileName), manifest);

        var config = MinimalConfig(WebLogicVersion.WLS_12c);
        config.PayloadBasePath = _root;
        config.Components = InstallationComponents.JDK;
        config.PayloadAcquisition.ValidateChecksums = true;

        var report = await _sut.ValidateAndResolveAsync(config);

        report.CanProceed.Should().BeFalse();
        report.Entries.Should().Contain(e => e.ChecksumStatus == PayloadChecksumStatus.Mismatch);
    }

    [Fact]
    public void PatternMatcher_matches_infrastructure_jar()
    {
        var dir = Path.Combine(_root, "patterns");
        Directory.CreateDirectory(dir);
        var jar = Path.Combine(dir, "fmw_12.2.1.4.0_infrastructure.jar");
        File.WriteAllText(jar, "x");

        var match = LocalPayloadPatternMatcher.FindBestMatch(dir, ["*infrastructure*.jar"]);
        match.Should().Be(jar);
    }

    private static DeploymentConfiguration MinimalConfig(WebLogicVersion version) => new()
    {
        WebLogicVersion = version,
        PayloadAcquisition = new PayloadAcquisitionConfiguration
        {
            UseLocalRepositoryOnly = true,
            AutoDownloadMissing = false,
            ValidateChecksums = false
        }
    };
}
