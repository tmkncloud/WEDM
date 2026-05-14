using Microsoft.Win32;

namespace WEDM.Bootstrapper;

/// <summary>
/// Lightweight host bootstrapper: prerequisite checks and optional hand-off to the enterprise installer (Inno Setup / MSI).
/// Does not download payloads. Exit codes: 0 OK, 10 missing installer, 20 VC++ not detected, 30 invalid args.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitMissingInstaller = 10;
    private const int ExitVcMissing = 20;
    private const int ExitBadArgs = 30;

    private static int Main(string[] args)
    {
        var silent = args.Any(a => string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
        var offline = args.FirstOrDefault(a => a.StartsWith("--offlineMediaPath=", StringComparison.OrdinalIgnoreCase));

        if (args.Any(a => string.Equals(a, "/?", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage(silent);
            return ExitOk;
        }

        if (!silent)
            Console.WriteLine("WEDM bootstrapper — prerequisite validation");

        var vcOk = IsVcRedistInstalled();
        if (!vcOk)
        {
            Console.Error.WriteLine("Visual C++ 2015–2022 x64 runtime not detected in registry.");
            Console.Error.WriteLine("Install VC++ x64 before Oracle middleware tiers, or use an installer bundle that chains the redistributable.");
            if (!silent)
                Console.WriteLine("Continuing anyway — WEDM self-contained host may still run.");
        }

        var setupName = args.FirstOrDefault(a => a.StartsWith("--setup=", StringComparison.OrdinalIgnoreCase)) is { } s
            ? s["--setup=".Length..].Trim('"')
            : "WEDM.Setup.exe";

        var baseDir = AppContext.BaseDirectory;
        var setupPath = Path.GetFullPath(Path.Combine(baseDir, setupName));
        if (!File.Exists(setupPath))
        {
            Console.Error.WriteLine($"Installer not found: {setupPath}");
            Console.Error.WriteLine("Place WEDM.Setup.exe next to this bootstrapper or pass --setup=<path>.");
            return ExitMissingInstaller;
        }

        if (args.Any(a => string.Equals(a, "--validate-only", StringComparison.OrdinalIgnoreCase)))
            return vcOk ? ExitOk : ExitVcMissing;

        if (args.Any(a => string.Equals(a, "/install", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase)))
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = setupPath,
                UseShellExecute = true
            };
            if (silent)
                psi.ArgumentList.Add("/VERYSILENT");
            if (offline is not null)
                psi.ArgumentList.Add(offline);
            try
            {
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to start installer: {ex.Message}");
                return ExitBadArgs;
            }

            return ExitOk;
        }

        PrintUsage(silent);
        return ExitOk;
    }

    private static void PrintUsage(bool silent)
    {
        if (silent) return;
        Console.WriteLine("""
            Usage:
              WEDM.Bootstrap.exe [--validate-only] [--silent] [--install] [--setup=WEDM.Setup.exe] [--offlineMediaPath=...]
            Examples:
              WEDM.Bootstrap.exe --validate-only
              WEDM.Bootstrap.exe --install --silent
            """);
    }

    private static bool IsVcRedistInstalled()
    {
        string[] paths =
        [
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
            @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
        ];

        foreach (var p in paths)
        {
            using var key = Registry.LocalMachine.OpenSubKey(p);
            if (key?.GetValue("Installed")?.ToString() == "1")
                return true;
        }

        return false;
    }
}
