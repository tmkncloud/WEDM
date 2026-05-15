using FluentAssertions;
using WEDM.Domain.Models;
using WEDM.Engine.Execution;
using Xunit;

namespace WEDM.Engine.Tests.Execution;

/// <summary>
/// Tests for the __WEDM_EXIT sentinel pattern emitted by
/// <see cref="WlstPowerShellEnvironment.BuildWlstLaunchBody"/>.
///
/// These tests verify the PowerShell body text produced by the builder — they
/// do NOT execute PowerShell (which would require a real runspace).
/// </summary>
public sealed class WlstExitSentinelTests
{
    private const string WlstCmd    = @"C:\Oracle\MW\wlserver\common\bin\wlst.cmd";
    private const string ScriptPath = @"C:\Temp\create_domain.py";

    private static WlstExecutionEnvironment DefaultEnv() => new()
    {
        OracleHome = @"C:\Oracle\MW",
        JavaHome   = @"C:\Java\jdk-8u202",
    };

    // ── Test 1 ────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildWlstLaunchBody_EmitsWedmExitSentinel()
    {
        var body = WlstPowerShellEnvironment.BuildWlstLaunchBody(WlstCmd, ScriptPath, DefaultEnv());

        // The sentinel write line must be present verbatim
        body.Should().Contain("Write-Output \"__WEDM_EXIT:$__wedm_rc\"",
            because: "PowerShellExecutor reads this marker to determine the true exit code");

        // The variable must also be assigned
        body.Should().Contain("$__wedm_rc =",
            because: "the sentinel variable must be calculated before being emitted");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildWlstLaunchBody_SentinelCaptures_NonZeroExitCode()
    {
        var body = WlstPowerShellEnvironment.BuildWlstLaunchBody(WlstCmd, ScriptPath, DefaultEnv());

        // The body must read $p.ExitCode to handle non-zero exit codes
        body.Should().Contain("$p.ExitCode",
            because: "the sentinel logic must propagate the child process exit code");

        // Null guard: when Start-Process fails to launch, $p may be null
        body.Should().Contain("$null -eq $p",
            because: "null guard prevents NullReferenceException if Start-Process returns nothing");
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildWlstLaunchBody_WithPreamble_InjectsPreambleBeforeProcess()
    {
        const string preamble = "Write-Output 'PRE-FLIGHT CHECK'";

        var body = WlstPowerShellEnvironment.BuildWlstLaunchBody(WlstCmd, ScriptPath, DefaultEnv(), preamble);

        // Preamble must appear in the body
        body.Should().Contain(preamble,
            because: "the caller-supplied preamble must be included verbatim");

        // Preamble must come BEFORE Start-Process
        var preambleIdx    = body.IndexOf(preamble,    StringComparison.Ordinal);
        var startProcessIdx = body.IndexOf("Start-Process", StringComparison.Ordinal);

        preambleIdx.Should().BeGreaterThanOrEqualTo(0);
        startProcessIdx.Should().BeGreaterThan(preambleIdx,
            because: "preamble is injected before the Start-Process call");
    }

    // ── Bonus: null environment still builds valid body ───────────────────────

    [Fact]
    public void BuildWlstLaunchBody_NullEnvironment_StillEmitsSentinel()
    {
        var body = WlstPowerShellEnvironment.BuildWlstLaunchBody(WlstCmd, ScriptPath, environment: null);

        body.Should().Contain("__WEDM_EXIT:$__wedm_rc");
        body.Should().Contain("Start-Process");
    }

    // ── Bonus: exit statement terminates the script with child rc ─────────────

    [Fact]
    public void BuildWlstLaunchBody_EndsWithExitStatement()
    {
        var body = WlstPowerShellEnvironment.BuildWlstLaunchBody(WlstCmd, ScriptPath, DefaultEnv());

        body.TrimEnd().Should().EndWith("exit $__wedm_rc",
            because: "the enclosing PowerShell process must propagate the child exit code to the caller");
    }
}
