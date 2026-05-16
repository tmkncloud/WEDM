using System.Text.Json;
using FluentAssertions;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Deployment;
using Xunit;

namespace Orchestration.Integration.Tests;

public sealed class DeploymentLockIntegrationTests : IDisposable
{
    private readonly string _lockRoot;
    private readonly DeploymentLockService _locks;

    public DeploymentLockIntegrationTests()
    {
        _lockRoot = Path.Combine(Path.GetTempPath(), "wedm-lock-it", Guid.NewGuid().ToString("N"));
        _locks    = new DeploymentLockService(_lockRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_lockRoot)) Directory.Delete(_lockRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Concurrent_acquire_second_session_fails()
    {
        var config = new DeploymentConfiguration
        {
            Name = "lock-test",
            Paths = new PathConfiguration
            {
                MiddlewareHome  = @"C:\oracle\middleware",
                OracleInventory = @"C:\Oracle\Inventory",
                DomainBase      = @"C:\oracle\domains"
            },
            Domain = new DomainConfiguration { DomainName = "test_domain" }
        };

        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        var first = await _locks.TryAcquireAsync(config, s1);
        first.Acquired.Should().BeTrue();

        var second = await _locks.TryAcquireAsync(config, s2);
        second.Acquired.Should().BeFalse();
        second.ConflictingLocks.Should().NotBeEmpty();

        await _locks.ReleaseAsync(s1);
        var third = await _locks.TryAcquireAsync(config, s2);
        third.Acquired.Should().BeTrue();
    }

    [Fact]
    public async Task CleanupStaleLocks_removes_stale_metadata()
    {
        var config = new DeploymentConfiguration
        {
            Paths = new PathConfiguration { MiddlewareHome = @"C:\oracle\stale-cleanup-test" }
        };
        var sessionId = Guid.NewGuid();
        (await _locks.TryAcquireAsync(config, sessionId)).Acquired.Should().BeTrue();

        foreach (var metaPath in Directory.GetFiles(_lockRoot, "*.meta"))
        {
            var json  = await File.ReadAllTextAsync(metaPath);
            var stale = JsonSerializer.Deserialize<DeploymentLockDescriptor>(json, DeploymentJsonOptions.Create())!;
            stale.LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-8);
            stale.OwnerProcessId  = 999999;
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(stale, DeploymentJsonOptions.Create()));
        }

        var removed = await _locks.CleanupStaleLocksAsync(TimeSpan.FromMinutes(1));
        removed.Should().BeGreaterThan(0);
        Directory.GetFiles(_lockRoot, "*.meta").Should().BeEmpty();
    }
}
