using WEDM.Domain.Models;
using WEDM.Engine.Execution;
using Xunit;

namespace WEDM.Engine.Tests.Execution;

public sealed class WlstExecutionServiceTests
{
    [Fact]
    public async Task ExecuteScriptAsync_DryRun_DoesNotRequireWlstBinary()
    {
        var temp = Path.Combine(Path.GetTempPath(), "wedm-wlst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var script = Path.Combine(temp, "test.py");
        await File.WriteAllTextAsync(script, "print('hello')\nexit()");

        var service = new WlstExecutionService(new FakePowerShellExecutor());
        var record = await service.ExecuteScriptAsync(
            @"C:\nonexistent\wlst.cmd",
            script,
            credentials: null,
            dryRun: true,
            logDirectory: temp);

        Assert.True(record.DryRun);
        Assert.True(record.Success);
        Assert.Contains("DRY-RUN", record.StdoutExcerpt ?? "");

        Directory.Delete(temp, true);
    }

    private sealed class FakePowerShellExecutor : Domain.Interfaces.IPowerShellExecutor
    {
        public event EventHandler<string>? OutputReceived;
        public event EventHandler<string>? ErrorReceived;

        public Task<Domain.Interfaces.PowerShellResult> ExecuteCommandAsync(
            string command, string? workingDirectory = null, bool runAsAdministrator = false,
            CancellationToken cancellationToken = default, TimeSpan? operationTimeout = null)
            => Task.FromResult(new Domain.Interfaces.PowerShellResult { Success = true, ExitCode = 0 });

        public Task<Domain.Interfaces.PowerShellResult> ExecuteModuleFunctionAsync(
            string modulePath, string functionName, Dictionary<string, object>? parameters = null,
            CancellationToken cancellationToken = default, TimeSpan? operationTimeout = null)
            => Task.FromResult(new Domain.Interfaces.PowerShellResult { Success = true, ExitCode = 0 });

        public Task<Domain.Interfaces.PowerShellResult> ExecuteScriptAsync(
            string scriptPath, Dictionary<string, object>? parameters = null, string? workingDirectory = null,
            bool runAsAdministrator = false, CancellationToken cancellationToken = default, TimeSpan? operationTimeout = null)
            => Task.FromResult(new Domain.Interfaces.PowerShellResult { Success = true, ExitCode = 0 });
    }
}
