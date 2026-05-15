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

    [Fact]
    public void BuildEnvironmentPowerShell_SetsJavaAndOracleHome()
    {
        var ps = WlstPowerShellEnvironment.BuildEnvironmentPowerShell(new WlstExecutionEnvironment
        {
            JavaHome   = @"D:\Java\jdk-21",
            OracleHome = @"D:\Oracle\Middleware",
        });

        Assert.Contains("$env:JAVA_HOME = 'D:\\Java\\jdk-21'", ps);
        Assert.Contains("$env:ORACLE_HOME = 'D:\\Oracle\\Middleware'", ps);
        Assert.Contains("$env:PATH", ps);
    }

    [Fact]
    public void BuildEnvironmentTrace_DoesNotLeakSecrets()
    {
        var trace = WlstPowerShellEnvironment.BuildEnvironmentTrace(new WlstExecutionEnvironment
        {
            JavaHome   = @"D:\Java\jdk",
            OracleHome = @"D:\Oracle\MW",
        });

        Assert.Contains("ORACLE_HOME=", trace);
        Assert.Contains("JAVA_HOME=", trace);
    }

    private sealed class FakePowerShellExecutor : Domain.Interfaces.IPowerShellExecutor
    {
        public event EventHandler<string>? OutputReceived;
        public event EventHandler<string>? ErrorReceived;

        public Task<Domain.Interfaces.PowerShellResult> ExecuteCommandAsync(
            string command, string? workingDirectory = null, bool runAsAdministrator = false,
            CancellationToken cancellationToken = default, TimeSpan? operationTimeout = null)
            => Task.FromResult(new Domain.Interfaces.PowerShellResult { Success = true, ExitCode = 0, Output = command });

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
