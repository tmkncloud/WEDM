using FluentAssertions;
using WEDM.Infrastructure.Security;
using Xunit;

namespace WEDM.Infrastructure.Tests.Security;

public sealed class DeploymentSecretLifecycleTests
{
    [Fact]
    public void CleanupSession_removes_tracked_temp_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wedm-secret-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var secretFile = Path.Combine(dir, "wedm_rcu_test.properties");
        File.WriteAllText(secretFile, "dbPassword=SuperSecret123");

        var svc = new DeploymentSecretLifecycleService();
        svc.TrackTempFile(secretFile);
        svc.CleanupSession(dir);

        File.Exists(secretFile).Should().BeFalse();
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
