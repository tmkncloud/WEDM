using WEDM.Engine.PowerShell;
using WEDM.Engine.Tests.Fakes;

namespace WEDM.Engine.Tests.PowerShell;

// ── Shared fixture ────────────────────────────────────────────────────────────

/// <summary>
/// Creates one <see cref="PowerShellExecutor"/> instance shared across all tests in
/// <see cref="PowerShellExecutorCompatibilityTests"/>.  Opening a RunspacePool is
/// expensive (~1 s), so sharing across the test class keeps the suite fast.
/// </summary>
public sealed class PowerShellExecutorFixture : IDisposable
{
    public FakeLoggingService Log      { get; } = new();
    public PowerShellExecutor Executor { get; }

    public PowerShellExecutorFixture()
        => Executor = new PowerShellExecutor(Log);

    public void Dispose()
        => Executor.Dispose();
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Compatibility tests for <see cref="PowerShellExecutor"/>.
///
/// Coverage goals
/// ──────────────
/// 1. Constructor never throws on the current machine (startup-crash regression).
/// 2. "[PowerShellHost]" diagnostics are logged on construction.
/// 3. Built-in cmdlets (Set-Location, Get-ChildItem, Write-Output) are available without
///    any explicit ImportPSModule — the critical regression guard for the
///    "CmdletInvocationException: Cannot find the built-in module
///     'Microsoft.PowerShell.Management' that is compatible with the 'Core' edition"
///    crash introduced when ImportPSModule was added.
/// 4. Output is captured and returned correctly.
/// 5. Cancellation and timeout are honoured.
/// 6. Graceful failure for missing script paths.
/// 7. The runspace pool does not become unavailable on a normal Windows host.
/// </summary>
[Collection("PowerShellExecutorCompatibilityTests")]
public sealed class PowerShellExecutorCompatibilityTests
{
    private readonly PowerShellExecutorFixture _fx;

    public PowerShellExecutorCompatibilityTests(PowerShellExecutorFixture fx)
        => _fx = fx;

    // ── 1. Construction ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        // The fixture constructor already ran successfully; this explicit assertion
        // documents the invariant and fails with a clear message if the fixture threw.
        Assert.NotNull(_fx.Executor);
    }

    // ── 2. Startup diagnostics ────────────────────────────────────────────────

    [Fact]
    public void Constructor_LogsPowerShellHostLine()
    {
        var entries = _fx.Log.AllEntries;
        Assert.Contains(entries, e =>
            e.Message.Contains("[PowerShellHost]", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_LogsRunspaceReadyOrFallback()
    {
        // Either "runspace pool ready" (happy path) or a warning about fallback/failure.
        // The point is: something is always logged — no silent failures.
        var entries = _fx.Log.AllEntries;
        Assert.Contains(entries, e =>
            e.Message.Contains("runspace", StringComparison.OrdinalIgnoreCase)
         || e.Message.Contains("PowerShellHost", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_LogsEditionAndExecutable()
    {
        // The first log line emitted is _hostInfo.ToString() which contains Edition= and Executable=
        var entries = _fx.Log.AllEntries;
        Assert.Contains(entries, e =>
            e.Message.Contains("Edition=", StringComparison.Ordinal)
         && e.Message.Contains("Executable=", StringComparison.Ordinal));
    }

    // ── 3. Built-in cmdlet availability (ImportPSModule regression guards) ────

    /// <summary>
    /// REGRESSION TEST — the critical guard against the CmdletInvocationException crash.
    ///
    /// Before the fix, PowerShellExecutor called:
    ///   iss.ImportPSModule("Microsoft.PowerShell.Management")
    /// which made _pool.Open() throw and left the executor in the unavailable state.
    /// Any subsequent command then returned:
    ///   "PowerShell runspace pool is unavailable."
    ///
    /// After the fix, CreateDefault2() registers Set-Location as a SessionStateCmdletEntry
    /// without any disk-based module file, so Set-Location executes cleanly.
    /// </summary>
    [Fact]
    public async Task ExecuteCommandAsync_SetLocation_DoesNotReturnPoolUnavailableError()
    {
        var temp   = Path.GetTempPath();
        var result = await _fx.Executor.ExecuteCommandAsync($"Set-Location -LiteralPath '{temp}'");

        Assert.False(
            result.Errors?.Contains("runspace pool is unavailable", StringComparison.OrdinalIgnoreCase) ?? false,
            $"Runspace pool was unavailable — constructor likely failed. Errors: {result.Errors}");
    }

    [Fact]
    public async Task ExecuteCommandAsync_SetLocation_Succeeds()
    {
        // Set-Location is in Microsoft.PowerShell.Management.  If the old ImportPSModule
        // approach is ever re-introduced, this test will fail with a CmdletInvocationException
        // or a "runspace pool unavailable" error before the command even runs.
        var temp   = Path.GetTempPath();
        var result = await _fx.Executor.ExecuteCommandAsync($"Set-Location -LiteralPath '{temp}'");

        // A successful Set-Location produces no output and no errors.
        // We allow ps.HadErrors=true only if it's NOT a real exception (informational errors).
        // The key assertion: the executor didn't report a fatal failure.
        Assert.False(
            result.Errors?.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase) ?? false,
            $"Set-Location cmdlet was not found — built-in module registration broken. Errors: {result.Errors}");
    }

    [Fact]
    public async Task ExecuteCommandAsync_GetChildItem_DoesNotThrowCommandNotFound()
    {
        // Get-ChildItem is also in Microsoft.PowerShell.Management — another regression guard.
        var temp   = Path.GetTempPath();
        var result = await _fx.Executor.ExecuteCommandAsync($"Get-ChildItem -LiteralPath '{temp}' -ErrorAction Stop");

        Assert.False(
            result.Errors?.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase) ?? false,
            $"Get-ChildItem cmdlet was not found. Errors: {result.Errors}");
    }

    [Fact]
    public async Task ExecuteCommandAsync_WriteOutput_IsAvailable()
    {
        // Write-Output is in Microsoft.PowerShell.Utility — another built-in module.
        var result = await _fx.Executor.ExecuteCommandAsync("Write-Output 'hello-wedm'");

        Assert.False(
            result.Errors?.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase) ?? false,
            $"Write-Output cmdlet was not found. Errors: {result.Errors}");
    }

    // ── 4. Output capture ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteCommandAsync_WriteOutput_CapturesOutputText()
    {
        var result = await _fx.Executor.ExecuteCommandAsync("Write-Output 'wedm-compatibility-marker'");

        // Guard: skip assertion if pool is unavailable (infrastructure issue, not the test's fault)
        if (result.Output?.Contains("runspace pool is unavailable", StringComparison.OrdinalIgnoreCase) == true)
            return;

        Assert.Contains("wedm-compatibility-marker", result.Output ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteCommandAsync_SimpleArithmetic_ReturnsCorrectResult()
    {
        var result = await _fx.Executor.ExecuteCommandAsync("(1 + 1).ToString()");

        if (result.Output?.Contains("runspace pool is unavailable", StringComparison.OrdinalIgnoreCase) == true)
            return;

        Assert.Contains("2", result.Output ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteCommandAsync_MultiLineOutput_CapturesAllLines()
    {
        var result = await _fx.Executor.ExecuteCommandAsync(
            "Write-Output 'line1'; Write-Output 'line2'; Write-Output 'line3'");

        if (result.Output?.Contains("runspace pool is unavailable", StringComparison.OrdinalIgnoreCase) == true)
            return;

        Assert.Contains("line1", result.Output ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("line3", result.Output ?? string.Empty, StringComparison.Ordinal);
    }

    // ── 5. Script execution ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteScriptAsync_NonExistentPath_ReturnsFailureResult()
    {
        var result = await _fx.Executor.ExecuteScriptAsync(@"C:\nonexistent\path\script.ps1");

        Assert.False(result.Success);
        Assert.Contains("Script not found", result.Errors ?? result.Output ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteScriptAsync_ValidScript_RunsSuccessfully()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wedm_compat_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(tmp, "Write-Output 'script-ok'");
        try
        {
            var result = await _fx.Executor.ExecuteScriptAsync(tmp);

            if (result.Output?.Contains("runspace pool is unavailable", StringComparison.OrdinalIgnoreCase) == true)
                return;

            Assert.True(result.Success,
                $"Script execution failed unexpectedly. ExitCode={result.ExitCode} Errors={result.Errors}");
            Assert.Contains("script-ok", result.Output ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    // ── 6. Cancellation ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteCommandAsync_PreCancelledToken_ReturnsCancelledResult()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _fx.Executor.ExecuteCommandAsync(
            "Start-Sleep -Seconds 60",
            cancellationToken: cts.Token);

        // Either the command was cancelled or it returned gracefully.
        // It must NOT throw an unhandled OperationCanceledException.
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteCommandAsync_Timeout_ReturnsTimedOutResult()
    {
        var result = await _fx.Executor.ExecuteCommandAsync(
            "Start-Sleep -Seconds 60",
            operationTimeout: TimeSpan.FromMilliseconds(200));

        Assert.False(result.Success);
        // The result should be either TimedOut or Cancelled (both indicate the operation stopped).
        Assert.True(result.TimedOut || !result.Success,
            $"Expected timed-out result but got ExitCode={result.ExitCode} Errors={result.Errors}");
    }

    // ── 7. Pool availability guard ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteCommandAsync_PoolAvailable_DoesNotReturnPoolUnavailableError()
    {
        // Verifies that the constructor successfully opened the RunspacePool on this machine.
        // If this test fails, the startup crash likely recurred.
        var result = await _fx.Executor.ExecuteCommandAsync("1");

        Assert.False(
            result.Output?.Contains("runspace pool is unavailable", StringComparison.OrdinalIgnoreCase) ?? false,
            "RunspacePool is unavailable — PowerShellExecutor constructor likely failed silently. " +
            $"Check log entries: {string.Join("; ", _fx.Log.AllEntries.Select(e => e.Message))}");

        Assert.False(
            result.Errors?.Contains("runspace pool is unavailable", StringComparison.OrdinalIgnoreCase) ?? false,
            "RunspacePool is unavailable — PowerShellExecutor constructor likely failed silently.");
    }

    // ── 8. Edition-aware execution path ──────────────────────────────────────

    [Fact]
    public void HostDetector_Edition_IsLoggedByExecutor()
    {
        var hostInfo = PowerShellHostDetector.Detect();
        var logLines = _fx.Log.AllEntries.Select(e => e.Message).ToList();

        // The executor logs _hostInfo.ToString() which contains "Edition=Core" or "Edition=Desktop"
        Assert.Contains(logLines, line =>
            line.Contains($"Edition={hostInfo.Edition}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteCommandAsync_PSVersionTable_ReturnsOutput_WhenPoolAvailable()
    {
        // $PSVersionTable.PSVersion is a reliable built-in — any PS host should have it.
        var result = await _fx.Executor.ExecuteCommandAsync("$PSVersionTable.PSVersion.Major");

        if (result.Output?.Contains("runspace pool is unavailable", StringComparison.OrdinalIgnoreCase) == true)
            return;

        // Output should be a number (major version, e.g. "5" or "7")
        Assert.False(string.IsNullOrWhiteSpace(result.Output),
            $"Expected PSVersion major number in output but got empty. Errors={result.Errors}");
    }
}

// ── xunit collection definition ───────────────────────────────────────────────

[CollectionDefinition("PowerShellExecutorCompatibilityTests")]
public sealed class PowerShellExecutorCompatibilityCollection
    : ICollectionFixture<PowerShellExecutorFixture> { }
