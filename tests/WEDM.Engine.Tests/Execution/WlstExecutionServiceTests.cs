using WEDM.Domain.Interfaces;
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

        var service = new WlstExecutionService(new FakeExternalProcessRunner());
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
    public async Task ExecuteScriptAsync_ScriptNotFound_ReturnsFailure()
    {
        var service = new WlstExecutionService(new FakeExternalProcessRunner());
        var record = await service.ExecuteScriptAsync(
            @"C:\nonexistent\wlst.cmd",
            @"C:\nonexistent\script.py",
            credentials: null,
            dryRun: false,
            logDirectory: Path.GetTempPath());

        Assert.False(record.Success);
        Assert.Contains("Script not found", record.StderrExcerpt ?? "");
    }

    [Fact]
    public async Task ExecuteScriptAsync_WlstNotFound_ReturnsFailure()
    {
        var temp = Path.Combine(Path.GetTempPath(), "wedm-wlst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var script = Path.Combine(temp, "test.py");
        await File.WriteAllTextAsync(script, "exit()");

        try
        {
            var service = new WlstExecutionService(new FakeExternalProcessRunner());
            var record = await service.ExecuteScriptAsync(
                @"C:\nonexistent\wlst.cmd",
                script,
                credentials: null,
                dryRun: false,
                logDirectory: temp);

            Assert.False(record.Success);
            Assert.Contains("WLST not found", record.StderrExcerpt ?? "");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void BuildEnvironmentVariables_SetsJavaAndOracleHome()
    {
        var vars = WlstPowerShellEnvironment.BuildEnvironmentVariables(new WlstExecutionEnvironment
        {
            JavaHome   = @"D:\Java\jdk-21",
            OracleHome = @"D:\Oracle\Middleware",
        });

        Assert.True(vars.ContainsKey("JAVA_HOME"));
        Assert.Equal(@"D:\Java\jdk-21", vars["JAVA_HOME"]);
        Assert.True(vars.ContainsKey("ORACLE_HOME"));
        Assert.Equal(@"D:\Oracle\Middleware", vars["ORACLE_HOME"]);
        Assert.True(vars.ContainsKey("PATH"));
        Assert.StartsWith(@"D:\Java\jdk-21\bin;", vars["PATH"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildEnvironmentVariables_NullEnvironment_ReturnsEmptyDict()
    {
        var vars = WlstPowerShellEnvironment.BuildEnvironmentVariables(null);
        Assert.Empty(vars);
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

    // ── Test double ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fake ExternalProcessRunner that always returns success.
    /// Used for tests that only need the dry-run or validation paths.
    /// </summary>
    private sealed class FakeExternalProcessRunner : IExternalProcessRunner
    {
        public event EventHandler<string>? OutputReceived;
        public event EventHandler<string>? ErrorReceived;

        public Task<ExternalProcessResult> RunAsync(
            ExternalProcessOptions options,
            Action<string>?        onStdout          = null,
            Action<string>?        onStderr          = null,
            CancellationToken      cancellationToken = default)
        {
            var result = new ExternalProcessResult
            {
                ExitCode    = 0,
                Output      = $"[FAKE] {options.Label}",
                OutputLines = [$"[FAKE] {options.Label}"],
                Duration    = TimeSpan.FromMilliseconds(1),
                Pid         = 999,
            };
            // Suppress unused event warning
            _ = OutputReceived;
            _ = ErrorReceived;
            return Task.FromResult(result);
        }
    }
}
