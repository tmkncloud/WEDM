using Microsoft.Win32;
using WEDM.Domain.Interfaces;

namespace WEDM.Infrastructure.Registry;

/// <summary>
/// Encapsulates all Windows Registry operations required for Oracle middleware deployment.
/// Used to set JAVA_HOME, PATH, NLS_LANG, REPORTS_CLASSPATH, FORMS_PATH, and other
/// Oracle-specific registry values documented in the operational procedures.
/// Requires Administrator privileges.
/// </summary>
public sealed class WindowsRegistryService
{
    private readonly ILoggingService _log;

    public WindowsRegistryService(ILoggingService log) => _log = log;

    // ── Environment Variables (System-wide) ───────────────────────────────────

    public void SetSystemEnvironmentVariable(string name, string value)
    {
        _log.Info($"Registry: Setting SYSTEM env var '{name}' = '{value}'", "Registry");
        using var key = OpenSystemEnvKey(writable: true);
        key.SetValue(name, value, RegistryValueKind.ExpandString);
        BroadcastEnvironmentChange();
    }

    public string? GetSystemEnvironmentVariable(string name)
    {
        using var key = OpenSystemEnvKey(writable: false);
        return key.GetValue(name, null)?.ToString();
    }

    public void AppendToSystemPath(string pathToAppend)
    {
        var existing = GetSystemEnvironmentVariable("PATH") ?? string.Empty;
        if (!existing.Split(';').Any(p => p.Equals(pathToAppend, StringComparison.OrdinalIgnoreCase)))
        {
            var newPath = existing.TrimEnd(';') + ";" + pathToAppend;
            SetSystemEnvironmentVariable("PATH", newPath);
            _log.Info($"PATH updated: appended '{pathToAppend}'", "Registry");
        }
        else
        {
            _log.Info($"PATH already contains '{pathToAppend}' — skipped", "Registry");
        }
    }

    // ── Oracle-specific Registry Keys ─────────────────────────────────────────

    /// <summary>
    /// Set NLS_LANG for Oracle client (required for Arabic/multilingual Forms applications).
    /// Path: HKLM\SOFTWARE\ORACLE\KEY_<OracleHome>\NLS_LANG
    /// </summary>
    public void SetOracleNlsLang(string oracleKeyName, string nlsLang)
    {
        _log.Info($"Oracle Registry: Setting NLS_LANG = '{nlsLang}' under {oracleKeyName}", "Registry");
        var keyPath = $@"SOFTWARE\ORACLE\{oracleKeyName}";
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
        key.SetValue("NLS_LANG", nlsLang, RegistryValueKind.String);
    }

    /// <summary>
    /// Set Oracle Home key in registry (required by some Oracle tools for discovery).
    /// </summary>
    public void SetOracleHome(string oracleKeyName, string oracleHomePath)
    {
        var keyPath = $@"SOFTWARE\ORACLE\{oracleKeyName}";
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
        key.SetValue("ORACLE_HOME", oracleHomePath, RegistryValueKind.String);
        _log.Info($"Oracle HOME set: {oracleHomePath}", "Registry");
    }

    /// <summary>
    /// Add font directories to Oracle REPORTS_PATH registry entry.
    /// </summary>
    public void AppendReportsPath(string oracleKeyName, string pathToAdd)
    {
        var keyPath = $@"SOFTWARE\ORACLE\{oracleKeyName}";
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
        var existing = key.GetValue("REPORTS_PATH", string.Empty)?.ToString() ?? string.Empty;
        if (!existing.Split(';').Contains(pathToAdd, StringComparer.OrdinalIgnoreCase))
        {
            key.SetValue("REPORTS_PATH", existing.TrimEnd(';') + ";" + pathToAdd, RegistryValueKind.String);
            _log.Info($"REPORTS_PATH extended with: {pathToAdd}", "Registry");
        }
    }

    // ── JDK Detection ─────────────────────────────────────────────────────────

    /// <summary>
    /// Detect installed JDK from registry.
    /// Returns the JAVA_HOME path if found, null otherwise.
    /// </summary>
    public string? DetectInstalledJdk()
    {
        string[] searchPaths =
        [
            @"SOFTWARE\JavaSoft\Java Development Kit",
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\WOW6432Node\JavaSoft\Java Development Kit"
        ];

        foreach (var path in searchPaths)
        {
            try
            {
                using var jdkKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (jdkKey is null) continue;

                var currentVersion = jdkKey.GetValue("CurrentVersion")?.ToString();
                if (currentVersion is null) continue;

                using var versionKey = jdkKey.OpenSubKey(currentVersion);
                var javaHome = versionKey?.GetValue("JavaHome")?.ToString();
                if (javaHome is not null && Directory.Exists(javaHome))
                {
                    _log.Info($"Detected JDK {currentVersion} at: {javaHome}", "Registry");
                    return javaHome;
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"JDK registry scan path '{path}' failed: {ex.Message}", "Registry");
            }
        }
        return null;
    }

    // ── VC++ Redistributable Detection ────────────────────────────────────────

    public bool IsVcRedistInstalled(string minimumVersion = "14.0")
    {
        string[] searchPaths =
        [
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
            @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86"
        ];

        foreach (var path in searchPaths)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            if (key is not null)
            {
                var installed = key.GetValue("Installed")?.ToString();
                if (installed == "1") return true;
            }
        }
        return false;
    }

    // ── Private Helpers ───────────────────────────────────────────────────────

    private static RegistryKey OpenSystemEnvKey(bool writable)
    {
        return Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
            writable)
            ?? throw new InvalidOperationException("Cannot open system environment registry key.");
    }

    /// <summary>
    /// Broadcast WM_SETTINGCHANGE so running processes pick up env var changes immediately.
    /// </summary>
    private static void BroadcastEnvironmentChange()
    {
        // SendMessageTimeout with HWND_BROADCAST and WM_SETTINGCHANGE
        const int HWND_BROADCAST = 0xFFFF;
        const int WM_SETTINGCHANGE = 0x001A;
        NativeMethods.SendMessageTimeout(
            new nint(HWND_BROADCAST), WM_SETTINGCHANGE, nint.Zero,
            "Environment", 0x0002, 1000, out _);
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    internal static extern nint SendMessageTimeout(
        nint hWnd, uint Msg, nint wParam, string lParam,
        uint fuFlags, uint uTimeout, out nint lpdwResult);
}
