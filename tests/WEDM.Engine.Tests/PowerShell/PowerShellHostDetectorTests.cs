using WEDM.Engine.PowerShell;

namespace WEDM.Engine.Tests.PowerShell;

/// <summary>
/// Tests for <see cref="PowerShellHostDetector"/>.
///
/// Each test calls <see cref="PowerShellHostDetector.ResetForTests"/> before and after
/// to guarantee a clean slate regardless of test-runner execution order.
/// </summary>
public sealed class PowerShellHostDetectorTests : IDisposable
{
    public PowerShellHostDetectorTests()  => PowerShellHostDetector.ResetForTests();
    public void Dispose()                 => PowerShellHostDetector.ResetForTests();

    // ── Basic result shape ────────────────────────────────────────────────────

    [Fact]
    public void Detect_ReturnsNonNull()
    {
        var info = PowerShellHostDetector.Detect();
        Assert.NotNull(info);
    }

    [Fact]
    public void Detect_Edition_IsDesktopOrCore()
    {
        var info = PowerShellHostDetector.Detect();

        Assert.True(
            info.Edition == "Core" || info.Edition == "Desktop",
            $"Unexpected Edition: '{info.Edition}'. Expected 'Core' or 'Desktop'.");
    }

    [Fact]
    public void Detect_ExecutableName_MatchesEdition()
    {
        var info = PowerShellHostDetector.Detect();

        if (info.Edition == "Core")
            Assert.Equal("pwsh.exe", info.ExecutableName);
        else
            Assert.Equal("powershell.exe", info.ExecutableName);
    }

    [Fact]
    public void Detect_UsingFallback_FalseWhenCoreEdition()
    {
        var info = PowerShellHostDetector.Detect();

        if (info.Edition == "Core")
            Assert.False(info.UsingFallback,
                "UsingFallback must be false when pwsh.exe is the preferred executable.");
    }

    [Fact]
    public void Detect_UsingFallback_TrueWhenDesktopEdition()
    {
        var info = PowerShellHostDetector.Detect();

        if (info.Edition == "Desktop")
            Assert.True(info.UsingFallback,
                "UsingFallback must be true when falling back to Windows PowerShell 5.1.");
    }

    // ── Executable path ───────────────────────────────────────────────────────

    [Fact]
    public void Detect_Executable_IsNotNullOrWhiteSpace()
    {
        var info = PowerShellHostDetector.Detect();
        Assert.False(string.IsNullOrWhiteSpace(info.Executable),
            "Executable path must not be null or empty.");
    }

    [Fact]
    public void Detect_WhenExecutableContainsDirectory_FileExists()
    {
        var info = PowerShellHostDetector.Detect();

        // If the path is a simple filename (e.g. "powershell.exe"), skip the file-exists check —
        // the detector uses it as a last-resort name that Windows resolves via PATH at process launch.
        if (!Path.IsPathRooted(info.Executable))
            return;

        Assert.True(File.Exists(info.Executable),
            $"Detected executable '{info.Executable}' does not exist on disk.");
    }

    [Fact]
    public void Detect_WhenPwshAvailable_ExecutableExistsOnDisk()
    {
        var info = PowerShellHostDetector.Detect();

        // Only verify when the detector actually found pwsh via a well-known or PATH path.
        if (info.Edition != "Core" || !Path.IsPathRooted(info.Executable))
            return;

        Assert.True(File.Exists(info.Executable),
            $"pwsh.exe was detected at '{info.Executable}' but the file does not exist.");
        Assert.EndsWith("pwsh.exe", info.Executable, StringComparison.OrdinalIgnoreCase);
    }

    // ── Version string ────────────────────────────────────────────────────────

    [Fact]
    public void Detect_Version_IsNotNullOrWhiteSpace()
    {
        var info = PowerShellHostDetector.Detect();
        Assert.False(string.IsNullOrWhiteSpace(info.Version),
            "Version must not be null or empty.");
    }

    [Fact]
    public void Detect_WhenCoreEditionFound_VersionStartsWithSeven()
    {
        var info = PowerShellHostDetector.Detect();

        if (info.Edition != "Core")
            return; // pwsh not installed on this machine — nothing to assert

        // Product version for PS7 starts with "7." (could be "7.4.6.500" or "7.5.0")
        Assert.True(
            info.Version.StartsWith("7.", StringComparison.Ordinal)
         || info.Version.StartsWith("8.", StringComparison.Ordinal), // forward-compat
            $"Expected PS7+ version to start with '7.' or '8.', got '{info.Version}'.");
    }

    // ── Caching ───────────────────────────────────────────────────────────────

    [Fact]
    public void Detect_ResultIsCached_SameReferenceOnTwoCalls()
    {
        var first  = PowerShellHostDetector.Detect();
        var second = PowerShellHostDetector.Detect();
        Assert.Same(first, second);
    }

    [Fact]
    public void ResetForTests_AllowsFreshDetectionOnNextCall()
    {
        var before = PowerShellHostDetector.Detect();
        PowerShellHostDetector.ResetForTests();
        var after = PowerShellHostDetector.Detect();

        // Objects are different instances (re-detected) but logically equivalent
        Assert.NotSame(before, after);
        Assert.Equal(before.Edition,        after.Edition);
        Assert.Equal(before.ExecutableName, after.ExecutableName);
    }

    // ── ToString diagnostics ──────────────────────────────────────────────────

    [Fact]
    public void ToString_ContainsEdition()
    {
        var info = PowerShellHostDetector.Detect();
        Assert.Contains("Edition=", info.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ContainsVersion()
    {
        var info = PowerShellHostDetector.Detect();
        Assert.Contains("Version=", info.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ContainsExecutable()
    {
        var info = PowerShellHostDetector.Detect();
        Assert.Contains("Executable=", info.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ContainsUsingFallback()
    {
        var info = PowerShellHostDetector.Detect();
        Assert.Contains("UsingFallback=", info.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ContainsModuleImportSkipped()
    {
        var info = PowerShellHostDetector.Detect();
        Assert.Contains("ModuleImportSkipped=", info.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ContainsRestrictedMode()
    {
        var info = PowerShellHostDetector.Detect();
        Assert.Contains("RestrictedMode=", info.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_StartsWithPowerShellHostPrefix()
    {
        var info = PowerShellHostDetector.Detect();
        Assert.StartsWith("[PowerShellHost]", info.ToString(), StringComparison.Ordinal);
    }

    // ── Default field values ──────────────────────────────────────────────────

    [Fact]
    public void Detect_ModuleImportSkipped_FalseByDefault()
    {
        // ModuleImportSkipped is informational and set only when an explicit ImportPSModule
        // attempt was made and failed.  The detector itself never sets this flag to true —
        // it is reserved for PowerShellExecutor to set via `with { ModuleImportSkipped = true }`.
        var info = PowerShellHostDetector.Detect();
        Assert.False(info.ModuleImportSkipped);
    }

    [Fact]
    public void Detect_RestrictedMode_FalseByDefault()
    {
        // RestrictedMode is set to true only when the RunspacePool falls back from
        // CreateDefault2() to CreateDefault().  The detector itself never sets it.
        var info = PowerShellHostDetector.Detect();
        Assert.False(info.RestrictedMode);
    }
}
