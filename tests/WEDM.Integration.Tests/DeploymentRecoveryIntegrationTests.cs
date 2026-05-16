using FluentAssertions;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Deployment;
using WEDM.Infrastructure.Persistence;
using Xunit;

namespace Orchestration.Integration.Tests;

public sealed class DeploymentRecoveryIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly JsonDeploymentSessionStore _store;

    public DeploymentRecoveryIntegrationTests()
    {
        _root  = Path.Combine(Path.GetTempPath(), "wedm-recovery-it", Guid.NewGuid().ToString("N"));
        _store = new JsonDeploymentSessionStore(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task SaveAndLoad_round_trips_session_state()
    {
        var sessionId = Guid.NewGuid();
        var state = new DeploymentSessionState
        {
            SessionId       = sessionId,
            ConfigurationId = Guid.NewGuid(),
            LifecycleStatus = DeploymentLifecycleStatus.InProgress,
            StartedAt       = DateTimeOffset.UtcNow,
            Configuration   = new DeploymentConfiguration { Name = "test" },
            Steps           =
            [
                DeploymentStepSnapshot.FromStep(new DeploymentStep
                {
                    Name = "InstallJDK",
                    Sequence = 1,
                    Status = StepStatus.Succeeded
                })
            ]
        };

        await _store.SaveAsync(state);
        var loaded = await _store.LoadAsync(sessionId);

        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be(sessionId);
        loaded.Steps.Should().ContainSingle(s => s.Name == "InstallJDK" && s.Status == StepStatus.Succeeded);
    }

    [Fact]
    public async Task Load_corrupt_file_quarantines_and_throws()
    {
        var sessionId = Guid.NewGuid();
        var path = Path.Combine(_root, JsonDeploymentSessionStore.SessionsSubdir, $"{sessionId:N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not valid json");

        var act = async () => await _store.LoadAsync(sessionId);
        await act.Should().ThrowAsync<InvalidDataException>();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task AtomicFileWriter_survives_concurrent_writes()
    {
        var target = Path.Combine(_root, "atomic-test.txt");
        var tasks = Enumerable.Range(0, 20).Select(i =>
            AtomicFileWriter.WriteAllTextAsync(target, $"content-{i}"));
        await Task.WhenAll(tasks);
        var text = await File.ReadAllTextAsync(target);
        text.Should().StartWith("content-");
    }
}
