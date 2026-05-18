using System.Diagnostics;

namespace WEDM.Engine.PowerShell;

/// <summary>
/// Immutable snapshot of the host PowerShell environment detected at application startup.
/// </summary>
public sealed record PowerShellHostInfo
{
    /// <summary>"Desktop" (Windows PowerShell 5.1) or "Core" (PowerShell 7+).</summary>
    public string Edition           { get; init; } = "Unknown";

    /// <summary>Version string of the located executable (e.g. "7.4.6", "5.1.x").</summary>
    public string Version           { get; init; } = "Unknown";

    /// <summary>Full filesystem path to the preferred PowerShell host executable.</summary>
    public string Executable        { get; init; } = "powershell.exe";

    /// <summary>File-name component only (e.g. "pwsh.exe" or "powershell.exe").</summary>
    public string ExecutableName    { get; init; } = "powershell.exe";

    /// <summary>
    /// <c>true</c> when pwsh.exe was not found and Windows PowerShell 5.1 is the fallback.
    /// </summary>
    public bool UsingFallback       { get; init; }

    /// <summary>
    /// <c>true</c> when module imports were attempted but skipped due to compatibility errors.
    /// This is informational — execution continues via the default session state.
    /// </summary>
    public bool ModuleImportSkipped { get; init; }

    /// <summary>
    /// <c>true</c> when the runspace had to fall back to <see cref="InitialSessionState.CreateDefault"/>
    /// because <see cref="InitialSessionState.CreateDefault2"/> failed.
    /// </summary>
    public bool RestrictedMode      { get; init; }

    public override string ToString()
        => $"[PowerShellHost] Edition={Edition} Version={Version} Executable={Executable} "
         + $"UsingFallback={UsingFallback} ModuleImportSkipped={ModuleImportSkipped} RestrictedMode={RestrictedMode}";
}

/// <summary>
/// Detects the available PowerShell executable and edition at startup.
///
/// Preferred order
/// ───────────────
/// 1. pwsh.exe (PowerShell Core 7+)  — located via well-known paths then PATH
/// 2. powershell.exe (Windows PowerShell 5.1) — always available on Windows
///
/// Result is cached after the first call; subsequent calls are allocation-free.
/// </summary>
public static class PowerShellHostDetector
{
    private static PowerShellHostInfo? _cached;
    private static readonly object     _lock = new();

    // ── Public entry point ────────────────────────────────────────────────────

    public static PowerShellHostInfo Detect()
    {
        if (_cached is not null) return _cached;
        lock (_lock)
        {
            _cached ??= DetectInternal();
            return _cached;
        }
    }

    /// <summary>
    /// Forces a fresh detection pass (use in tests to reset state).
    /// </summary>
    internal static void ResetForTests()
    {
        lock (_lock) { _cached = null; }
    }

    // ── Detection logic ───────────────────────────────────────────────────────

    private static PowerShellHostInfo DetectInternal()
    {
        // ── 1. Prefer pwsh.exe (PowerShell 7+) ──────────────────────────────
        var pwsh = FindExecutable("pwsh.exe",
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7-preview", "pwsh.exe"),
            // ARM/x86 program files
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "PowerShell", "7", "pwsh.exe"),
        ]);

        if (pwsh is not null)
        {
            return new PowerShellHostInfo
            {
                Edition        = "Core",
                Version        = GetFileVersion(pwsh),
                Executable     = pwsh,
                ExecutableName = "pwsh.exe",
                UsingFallback  = false,
            };
        }

        // ── 2. Fall back to Windows PowerShell 5.1 ───────────────────────────
        var ps51 = FindExecutable("powershell.exe",
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
        ]) ?? "powershell.exe"; // always resolvable on Windows

        return new PowerShellHostInfo
        {
            Edition        = "Desktop",
            Version        = GetFileVersion(ps51),
            Executable     = ps51,
            ExecutableName = "powershell.exe",
            UsingFallback  = true,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Look for an executable by checking <paramref name="wellKnown"/> paths first,
    /// then every directory in the PATH environment variable.
    /// </summary>
    private static string? FindExecutable(string name, string[] wellKnown)
    {
        // Well-known locations (checked before PATH so we get the canonical install)
        foreach (var candidate in wellKnown)
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* skip inaccessible paths */ }
        }

        // Walk PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(segment.Trim(), name);
                if (File.Exists(full)) return full;
            }
            catch { /* skip malformed PATH entries */ }
        }

        return null;
    }

    private static string GetFileVersion(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return "unknown";
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            return fvi.ProductVersion ?? fvi.FileVersion ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
