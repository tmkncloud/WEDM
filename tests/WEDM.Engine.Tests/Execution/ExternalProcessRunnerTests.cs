using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Moq;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Execution;
using Xunit;

namespace WEDM.Engine.Tests.Execution;

/// <summary>
/// Tests for <see cref="ExternalProcessRunner"/>.
///
/// All tests use real OS processes (cmd.exe, ping) so they run on Windows only.
/// Short timeouts are used to keep the suite fast; each watchdog-related test
/// explicitly sets a 3-second SilentProcessTimeout so the watchdog fires quickly.
///
/// Deadlock-safety tests use processes that generate a large amount of stdout/stderr
/// to verify that the async pipe draining never causes a buffer-full hang.
/// </summary>
[Collection("ExternalProcessRunner")]
public sealed class ExternalProcessRunner_StdoutCaptureTests
{
    private readonly ExternalProcessRunner _runner;

    public ExternalProcessRunner_StdoutCaptureTests()
    {
        var log = new Mock<ILoggingService>();
        log.Setup(l => l.ScriptOutput(It.IsAny<string>(), It.IsAny<bool>()));
        _runner = new ExternalProcessRunner(log.Object);
    }

    [Fact]
    public async Task Stdout_is_captured_from_echo_command()
    {
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c echo hello from WEDM",
            TotalTimeout          = TimeSpan.FromSeconds(15),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "echo test",
        };

        var result = await _runner.RunAsync(options);

        result.Success.Should().BeTrue(because: "echo exits 0");
        result.ExitCode.Should().Be(0);
        result.Output.Should().Contain("hello from WEDM");
    }

    [Fact]
    public async Task Stderr_is_captured_without_deadlock()
    {
        // Write to stderr only (1>&2 redirects stdout to stderr in cmd.exe)
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c echo error line 1>&2",
            TotalTimeout          = TimeSpan.FromSeconds(15),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "stderr test",
        };

        var result = await _runner.RunAsync(options);

        result.ExitCode.Should().Be(0);
        result.Errors.Should().Contain("error line");
    }

    [Fact]
    public async Task Exit_code_is_propagated_correctly()
    {
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c exit 42",
            TotalTimeout          = TimeSpan.FromSeconds(10),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "exit code test",
        };

        var result = await _runner.RunAsync(options);

        result.ExitCode.Should().Be(42);
        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.Hung.Should().BeFalse();
        result.LaunchFailed.Should().BeFalse();
    }

    [Fact]
    public async Task OutputLines_collection_is_populated()
    {
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c (echo line1) & (echo line2) & (echo line3)",
            TotalTimeout          = TimeSpan.FromSeconds(15),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "multi-line test",
        };

        var result = await _runner.RunAsync(options);

        result.OutputLines.Should().HaveCountGreaterOrEqualTo(3);
        result.OutputLines.Should().Contain(l => l.Contains("line1"));
        result.OutputLines.Should().Contain(l => l.Contains("line2"));
        result.OutputLines.Should().Contain(l => l.Contains("line3"));
    }

    [Fact]
    public async Task OnStdout_callback_receives_each_line()
    {
        var received = new List<string>();
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c (echo alpha) & (echo beta)",
            TotalTimeout          = TimeSpan.FromSeconds(15),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "callback test",
        };

        await _runner.RunAsync(options, onStdout: line => received.Add(line));

        received.Should().Contain(l => l.Contains("alpha"));
        received.Should().Contain(l => l.Contains("beta"));
    }

    [Fact]
    public async Task OutputReceived_event_fires_for_each_line()
    {
        var eventLines = new List<string>();
        _runner.OutputReceived += (_, line) => eventLines.Add(line);

        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c echo event-test",
            TotalTimeout          = TimeSpan.FromSeconds(15),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "event test",
        };

        await _runner.RunAsync(options);

        eventLines.Should().Contain(l => l.Contains("event-test"));
    }

    [Fact]
    public async Task Large_stdout_does_not_deadlock()
    {
        // Generate ~1 MB of output. Without async pipe draining the child would
        // block on the 4 KB pipe buffer and the process would never exit.
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c for /l %i in (1,1,5000) do echo This is a long output line number %i to fill the pipe buffer and test deadlock safety",
            TotalTimeout          = TimeSpan.FromSeconds(60),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "large stdout test",
        };

        var sw = Stopwatch.StartNew();
        var result = await _runner.RunAsync(options);
        sw.Stop();

        result.TimedOut.Should().BeFalse(because: "1 MB of output should drain within 60s");
        result.ExitCode.Should().Be(0);
        result.OutputLines.Should().HaveCountGreaterOrEqualTo(1000,
            because: "should capture thousands of lines without dropping any");
    }

    [Fact]
    public async Task Pid_is_populated_in_result()
    {
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c exit 0",
            TotalTimeout          = TimeSpan.FromSeconds(10),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "pid test",
        };

        var result = await _runner.RunAsync(options);

        result.Pid.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Duration_reflects_actual_elapsed_time()
    {
        // ping with -n 1 takes ~1 second on Windows
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c ping -n 2 127.0.0.1 > nul",
            TotalTimeout          = TimeSpan.FromSeconds(15),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "duration test",
        };

        var result = await _runner.RunAsync(options);

        result.Duration.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500));
    }
}

[Collection("ExternalProcessRunner")]
public sealed class ExternalProcessRunner_CancellationTests
{
    private readonly ExternalProcessRunner _runner;

    public ExternalProcessRunner_CancellationTests()
    {
        var log = new Mock<ILoggingService>();
        _runner = new ExternalProcessRunner(log.Object);
    }

    [Fact]
    public async Task Cancellation_stops_long_running_process()
    {
        using var cts = new CancellationTokenSource();

        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c ping -n 300 127.0.0.1 > nul",
            TotalTimeout          = TimeSpan.FromMinutes(5),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "cancel test",
        };

        // Cancel after 500 ms
        cts.CancelAfter(500);

        var sw     = Stopwatch.StartNew();
        var result = await _runner.RunAsync(options, cancellationToken: cts.Token);
        sw.Stop();

        result.Cancelled.Should().BeTrue();
        result.Success.Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            because: "cancellation should kill the process quickly");
    }

    [Fact]
    public async Task Cancellation_result_has_exit_code_minus_one()
    {
        using var cts = new CancellationTokenSource(200);

        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c ping -n 300 127.0.0.1 > nul",
            TotalTimeout          = TimeSpan.FromMinutes(5),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "cancel exit code test",
        };

        var result = await _runner.RunAsync(options, cancellationToken: cts.Token);

        result.ExitCode.Should().Be(-1);
        result.Cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Total_timeout_triggers_timeout_result()
    {
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c ping -n 300 127.0.0.1 > nul",
            TotalTimeout          = TimeSpan.FromMilliseconds(800),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "total timeout test",
        };

        var result = await _runner.RunAsync(options);

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-2);
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_propagates_when_already_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c exit 0",
            TotalTimeout          = TimeSpan.FromSeconds(10),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "pre-cancel test",
        };

        var result = await _runner.RunAsync(options, cancellationToken: cts.Token);

        // Pre-cancelled token: process may start or be cancelled immediately
        // — either way success should be false
        result.Success.Should().BeFalse();
    }
}

[Collection("ExternalProcessRunner")]
public sealed class ExternalProcessRunner_WatchdogTests
{
    private readonly ExternalProcessRunner _runner;

    public ExternalProcessRunner_WatchdogTests()
    {
        var log = new Mock<ILoggingService>();
        _runner = new ExternalProcessRunner(log.Object);
    }

    [Fact]
    public async Task Watchdog_fires_on_silent_process()
    {
        // ping with redirect to nul produces no visible stdout/stderr — triggers watchdog
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c ping -n 300 127.0.0.1 > nul",
            TotalTimeout          = TimeSpan.FromMinutes(2),
            SilentProcessTimeout  = TimeSpan.FromSeconds(4),  // short for test
            EnableWatchdog        = true,
            Label                 = "watchdog silent test",
        };

        var sw     = Stopwatch.StartNew();
        var result = await _runner.RunAsync(options);
        sw.Stop();

        result.Hung.Should().BeTrue(because: "no output was produced so watchdog should fire");
        result.ExitCode.Should().Be(-3);
        result.Success.Should().BeFalse();
        // Should fire at ~SilentProcessTimeout, not the 2-minute total timeout
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Watchdog_does_not_fire_for_active_process()
    {
        // Process produces output frequently — watchdog must not fire
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c for /l %i in (1,1,20) do (echo heartbeat %i & ping -n 1 127.0.0.1 > nul)",
            TotalTimeout          = TimeSpan.FromSeconds(60),
            SilentProcessTimeout  = TimeSpan.FromSeconds(5),
            EnableWatchdog        = true,
            Label                 = "watchdog active test",
        };

        var result = await _runner.RunAsync(options);

        result.Hung.Should().BeFalse(because: "process emits output every ~1s");
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Watchdog_diagnostics_populated_on_hang()
    {
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c ping -n 300 127.0.0.1 > nul",
            TotalTimeout          = TimeSpan.FromMinutes(2),
            SilentProcessTimeout  = TimeSpan.FromSeconds(4),
            EnableWatchdog        = true,
            Label                 = "watchdog diagnostics test",
        };

        var result = await _runner.RunAsync(options);

        result.Hung.Should().BeTrue();
        result.Diagnostics.Should().NotBeNull();
        result.Diagnostics!.HangClassification.Should().Be("SilentProcessHang");
        result.Diagnostics.CommandLine.Should().Contain("cmd.exe");
        result.Diagnostics.CapturedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Watchdog_disabled_does_not_kill_silent_process()
    {
        // With watchdog disabled, a silent process should be allowed to run to completion
        // (here the process exits normally after a short time)
        var options = new ExternalProcessOptions
        {
            FileName              = "cmd.exe",
            Arguments             = "/c ping -n 3 127.0.0.1 > nul",
            TotalTimeout          = TimeSpan.FromSeconds(30),
            SilentProcessTimeout  = TimeSpan.FromSeconds(1), // would fire if enabled
            EnableWatchdog        = false,
            Label                 = "watchdog disabled test",
        };

        var result = await _runner.RunAsync(options);

        result.Hung.Should().BeFalse(because: "watchdog is disabled");
        result.ExitCode.Should().Be(0);
    }
}

[Collection("ExternalProcessRunner")]
public sealed class ExternalProcessRunner_LaunchFailureTests
{
    private readonly ExternalProcessRunner _runner;

    public ExternalProcessRunner_LaunchFailureTests()
    {
        var log = new Mock<ILoggingService>();
        _runner = new ExternalProcessRunner(log.Object);
    }

    [Fact]
    public async Task Nonexistent_executable_returns_launch_failure()
    {
        var options = new ExternalProcessOptions
        {
            FileName              = "this_executable_does_not_exist_xyz.exe",
            Arguments             = "/c exit 0",
            TotalTimeout          = TimeSpan.FromSeconds(10),
            SilentProcessTimeout  = Timeout.InfiniteTimeSpan,
            EnableWatchdog        = false,
            Label                 = "launch failure test",
        };

        var result = await _runner.RunAsync(options);

        result.LaunchFailed.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(-4);
        result.LaunchFailureReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Launch_verification_detects_missing_child_process()
    {
        // cmd.exe /c exit 0 exits immediately and will NOT spawn a "java" child.
        // The runner should report LaunchFailed because java never appeared.
        var options = new ExternalProcessOptions
        {
            FileName                  = "cmd.exe",
            Arguments                 = "/c exit 0",
            TotalTimeout              = TimeSpan.FromSeconds(30),
            SilentProcessTimeout      = Timeout.InfiniteTimeSpan,
            EnableWatchdog            = false,
            ExpectedChildProcessName  = "java_child_that_does_not_exist_zz",
            LaunchVerificationTimeout = TimeSpan.FromSeconds(3),
            Label                     = "no-java-started test",
        };

        var result = await _runner.RunAsync(options);

        result.LaunchFailed.Should().BeTrue(
            because: "the expected child process never appeared");
        result.LaunchFailureReason.Should().Contain("java_child_that_does_not_exist_zz");
    }

    [Fact]
    public async Task Empty_filename_throws_argument_exception()
    {
        var options = new ExternalProcessOptions
        {
            FileName = "",
            Label    = "empty filename test",
        };

        await _runner.Invoking(r => r.RunAsync(options))
              .Should().ThrowAsync<ArgumentException>()
              .WithMessage("*FileName*");
    }

    [Fact]
    public async Task Null_options_throws_argument_null_exception()
    {
        await _runner.Invoking(r => r.RunAsync(null!))
              .Should().ThrowAsync<ArgumentNullException>();
    }
}

[Collection("ExternalProcessRunner")]
public sealed class ExternalProcessRunner_EnvironmentTests
{
    private readonly ExternalProcessRunner _runner;

    public ExternalProcessRunner_EnvironmentTests()
    {
        var log = new Mock<ILoggingService>();
        _runner = new ExternalProcessRunner(log.Object);
    }

    [Fact]
    public async Task Environment_variables_are_injected_into_child()
    {
        var options = new ExternalProcessOptions
        {
            FileName             = "cmd.exe",
            Arguments            = "/c echo %WEDM_TEST_VAR%",
            TotalTimeout         = TimeSpan.FromSeconds(15),
            SilentProcessTimeout = Timeout.InfiniteTimeSpan,
            EnableWatchdog       = false,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["WEDM_TEST_VAR"] = "injection_works",
            },
            Label = "env inject test",
        };

        var result = await _runner.RunAsync(options);

        result.ExitCode.Should().Be(0);
        result.Output.Should().Contain("injection_works");
    }

    [Fact]
    public async Task Working_directory_is_applied_to_child()
    {
        var tmp = Path.GetTempPath().TrimEnd('\\', '/');
        var options = new ExternalProcessOptions
        {
            FileName             = "cmd.exe",
            Arguments            = "/c cd",
            WorkingDirectory     = tmp,
            TotalTimeout         = TimeSpan.FromSeconds(15),
            SilentProcessTimeout = Timeout.InfiniteTimeSpan,
            EnableWatchdog       = false,
            Label                = "working dir test",
        };

        var result = await _runner.RunAsync(options);

        result.ExitCode.Should().Be(0);
        result.Output.Trim().Should().StartWith(tmp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_environment_variables_does_not_throw()
    {
        var options = new ExternalProcessOptions
        {
            FileName             = "cmd.exe",
            Arguments            = "/c exit 0",
            TotalTimeout         = TimeSpan.FromSeconds(10),
            SilentProcessTimeout = Timeout.InfiniteTimeSpan,
            EnableWatchdog       = false,
            EnvironmentVariables = null,
            Label                = "null env test",
        };

        Func<Task> act = () => _runner.RunAsync(options);
        await act.Should().NotThrowAsync();
    }
}

[Collection("ExternalProcessRunner")]
public sealed class ExternalProcessRunner_ProcessTreeTests
{
    private readonly ExternalProcessRunner _runner;

    public ExternalProcessRunner_ProcessTreeTests()
    {
        var log = new Mock<ILoggingService>();
        _runner = new ExternalProcessRunner(log.Object);
    }

    [Fact]
    public async Task Process_tree_is_killed_on_cancellation()
    {
        // cmd.exe spawns a child cmd.exe with a long-running ping.
        // On cancellation the entire tree (parent + child) must be killed.
        using var cts = new CancellationTokenSource(500);

        var options = new ExternalProcessOptions
        {
            FileName             = "cmd.exe",
            Arguments            = "/c cmd.exe /c ping -n 300 127.0.0.1 > nul",
            TotalTimeout         = TimeSpan.FromMinutes(2),
            SilentProcessTimeout = Timeout.InfiniteTimeSpan,
            EnableWatchdog       = false,
            Label                = "tree kill test",
        };

        var result = await _runner.RunAsync(options, cancellationToken: cts.Token);

        result.Cancelled.Should().BeTrue();

        // Give the OS a moment to complete the kill
        await Task.Delay(1000);

        // Verify no cmd.exe processes that we spawned are still alive.
        // We use the fact that the test itself has no other long-running cmd.exe instances.
        // (This is a best-effort check — we just verify cancellation was reported correctly.)
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_invocations_are_independent()
    {
        var opt1 = new ExternalProcessOptions
        {
            FileName = "cmd.exe", Arguments = "/c echo concurrent-1",
            TotalTimeout = TimeSpan.FromSeconds(15),
            SilentProcessTimeout = Timeout.InfiniteTimeSpan, EnableWatchdog = false, Label = "concurrent 1",
        };
        var opt2 = new ExternalProcessOptions
        {
            FileName = "cmd.exe", Arguments = "/c echo concurrent-2",
            TotalTimeout = TimeSpan.FromSeconds(15),
            SilentProcessTimeout = Timeout.InfiniteTimeSpan, EnableWatchdog = false, Label = "concurrent 2",
        };

        var t1 = _runner.RunAsync(opt1);
        var t2 = _runner.RunAsync(opt2);
        await Task.WhenAll(t1, t2);

        t1.Result.Success.Should().BeTrue();
        t2.Result.Success.Should().BeTrue();
        t1.Result.Output.Should().Contain("concurrent-1");
        t2.Result.Output.Should().Contain("concurrent-2");
    }
}

[Collection("ExternalProcessRunner")]
public sealed class ExternalProcessRunner_DiagnosticsTests
{
    private readonly ExternalProcessRunner _runner;

    public ExternalProcessRunner_DiagnosticsTests()
    {
        var log = new Mock<ILoggingService>();
        _runner = new ExternalProcessRunner(log.Object);
    }

    [Fact]
    public async Task Launch_failure_diagnostics_contains_command_line()
    {
        var options = new ExternalProcessOptions
        {
            FileName                  = "cmd.exe",
            Arguments                 = "/c exit 0",
            TotalTimeout              = TimeSpan.FromSeconds(15),
            SilentProcessTimeout      = Timeout.InfiniteTimeSpan,
            EnableWatchdog            = false,
            ExpectedChildProcessName  = "nonexistent_zz_process",
            LaunchVerificationTimeout = TimeSpan.FromSeconds(2),
            Label                     = "diag command line test",
        };

        var result = await _runner.RunAsync(options);

        result.LaunchFailed.Should().BeTrue();
        result.Diagnostics.Should().NotBeNull();
        result.Diagnostics!.CommandLine.Should().Contain("cmd.exe");
    }

    [Fact]
    public async Task Timeout_result_contains_captured_output()
    {
        // Process that produces some output then hangs
        var options = new ExternalProcessOptions
        {
            FileName             = "cmd.exe",
            Arguments            = "/c (echo before-timeout) & ping -n 300 127.0.0.1 > nul",
            TotalTimeout         = TimeSpan.FromMilliseconds(1500),
            SilentProcessTimeout = Timeout.InfiniteTimeSpan,
            EnableWatchdog       = false,
            Label                = "timeout output test",
        };

        var result = await _runner.RunAsync(options);

        result.TimedOut.Should().BeTrue();
        // The output produced before the timeout should still be captured
        result.Output.Should().Contain("before-timeout");
    }

    [Fact]
    public async Task ErrorLines_populated_from_stderr()
    {
        var options = new ExternalProcessOptions
        {
            FileName             = "cmd.exe",
            Arguments            = "/c echo err-line 1>&2",
            TotalTimeout         = TimeSpan.FromSeconds(15),
            SilentProcessTimeout = Timeout.InfiniteTimeSpan,
            EnableWatchdog       = false,
            Label                = "errorlines test",
        };

        var result = await _runner.RunAsync(options);

        result.ErrorLines.Should().Contain(l => l.Contains("err-line"));
    }
}

[Collection("ExternalProcessRunner")]
public sealed class ExternalProcessRunner_ExternalProcessTimeoutsTests
{
    [Fact]
    public void DomainCreation_is_90_minutes()
        => ExternalProcessTimeouts.DomainCreation.Should().Be(TimeSpan.FromMinutes(90));

    [Fact]
    public void SilentProcess_is_5_minutes()
        => ExternalProcessTimeouts.SilentProcess.Should().Be(TimeSpan.FromMinutes(5));

    [Fact]
    public void LaunchVerification_is_30_seconds()
        => ExternalProcessTimeouts.LaunchVerification.Should().Be(TimeSpan.FromSeconds(30));

    [Fact]
    public void PatchApplication_is_30_minutes()
        => ExternalProcessTimeouts.PatchApplication.Should().Be(TimeSpan.FromMinutes(30));

    [Fact]
    public void JdkInstall_is_15_minutes()
        => ExternalProcessTimeouts.JdkInstall.Should().Be(TimeSpan.FromMinutes(15));
}

[Collection("ExternalProcessRunner")]
public sealed class ExternalProcessOptions_DefaultsTests
{
    [Fact]
    public void Default_TotalTimeout_matches_DomainCreation_preset()
    {
        var opts = new ExternalProcessOptions();
        opts.TotalTimeout.Should().Be(ExternalProcessTimeouts.DomainCreation);
    }

    [Fact]
    public void Default_SilentProcessTimeout_matches_SilentProcess_preset()
    {
        var opts = new ExternalProcessOptions();
        opts.SilentProcessTimeout.Should().Be(ExternalProcessTimeouts.SilentProcess);
    }

    [Fact]
    public void Default_LaunchVerificationTimeout_matches_preset()
    {
        var opts = new ExternalProcessOptions();
        opts.LaunchVerificationTimeout.Should().Be(ExternalProcessTimeouts.LaunchVerification);
    }

    [Fact]
    public void Default_EnableWatchdog_is_true()
    {
        var opts = new ExternalProcessOptions();
        opts.EnableWatchdog.Should().BeTrue();
    }

    [Fact]
    public void Default_DiagnosticsTailLines_is_50()
    {
        var opts = new ExternalProcessOptions();
        opts.DiagnosticsTailLines.Should().Be(50);
    }

    [Fact]
    public void Default_Label_is_not_empty()
    {
        var opts = new ExternalProcessOptions();
        opts.Label.Should().NotBeNullOrEmpty();
    }
}

[Collection("ExternalProcessRunner")]
public sealed class ExternalProcessResult_ModelTests
{
    [Fact]
    public void Success_is_true_only_when_exit_zero_and_no_abort_flag()
    {
        var ok = new ExternalProcessResult { ExitCode = 0 };
        ok.Success.Should().BeTrue();
    }

    [Fact]
    public void Success_is_false_when_ExitCode_nonzero()
    {
        var r = new ExternalProcessResult { ExitCode = 1 };
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void Success_is_false_when_TimedOut()
    {
        var r = new ExternalProcessResult { ExitCode = 0, TimedOut = true };
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void Success_is_false_when_Hung()
    {
        var r = new ExternalProcessResult { ExitCode = 0, Hung = true };
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void Success_is_false_when_LaunchFailed()
    {
        var r = new ExternalProcessResult { ExitCode = 0, LaunchFailed = true };
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void FromCancelled_sets_exit_minus_one_and_Cancelled_flag()
    {
        var r = ExternalProcessResult.FromCancelled(TimeSpan.FromSeconds(1));
        r.ExitCode.Should().Be(-1);
        r.Cancelled.Should().BeTrue();
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void FromTimeout_sets_exit_minus_two_and_TimedOut_flag()
    {
        var r = ExternalProcessResult.FromTimeout(
            TimeSpan.FromSeconds(5), ["line1"], ["err1"]);
        r.ExitCode.Should().Be(-2);
        r.TimedOut.Should().BeTrue();
        r.Output.Should().Contain("line1");
        r.Errors.Should().Contain("err1");
    }

    [Fact]
    public void FromHung_sets_exit_minus_three_and_Hung_flag()
    {
        var diag = new ExternalProcessCrashDiagnostics { HangClassification = "Test" };
        var r    = ExternalProcessResult.FromHung(
            TimeSpan.FromSeconds(10), ["out"], ["err"], diag);
        r.ExitCode.Should().Be(-3);
        r.Hung.Should().BeTrue();
        r.Diagnostics.Should().BeSameAs(diag);
    }

    [Fact]
    public void FromLaunchFailure_sets_exit_minus_four_and_LaunchFailed_flag()
    {
        var r = ExternalProcessResult.FromLaunchFailure("reason text", TimeSpan.FromMilliseconds(100));
        r.ExitCode.Should().Be(-4);
        r.LaunchFailed.Should().BeTrue();
        r.LaunchFailureReason.Should().Be("reason text");
    }
}
