using System.Diagnostics;
using WEDM.Infrastructure.Registry;

namespace WEDM.Engine.Jdk;

/// <summary>Post-install validation — does not rely on installer exit code alone.</summary>
public sealed class JdkInstallValidator
{
    private readonly WindowsRegistryService _registry;

    public JdkInstallValidator(WindowsRegistryService registry) => _registry = registry;

    public JdkInstallValidationResult Validate(
        string targetJavaHome,
        string? installDirectoryRoot = null)
    {
        var checks = new List<string>();
        var javaHome = ResolveInstalledJavaHome(targetJavaHome, installDirectoryRoot);

        if (javaHome is null)
        {
            checks.Add("FAIL: Could not locate JAVA_HOME under target or install directory.");
            return new JdkInstallValidationResult { Passed = false, JavaHome = null, Checks = checks };
        }

        var javaExe  = Path.Combine(javaHome, "bin", "java.exe");
        var javacExe = Path.Combine(javaHome, "bin", "javac.exe");

        if (File.Exists(javaExe))
            checks.Add($"PASS: java.exe found at {javaExe}");
        else
            checks.Add($"FAIL: java.exe missing at {javaExe}");

        if (File.Exists(javacExe))
            checks.Add($"PASS: javac.exe found at {javacExe}");
        else
            checks.Add($"WARN: javac.exe missing at {javacExe} (JRE-only layout?)");

        var releaseFile = Path.Combine(javaHome, "release");
        if (File.Exists(releaseFile))
            checks.Add($"PASS: release file present: {releaseFile}");
        else if (Directory.Exists(Path.Combine(javaHome, "jre")))
            checks.Add("PASS: Java 8 layout detected (jre/ subdirectory).");
        else if (File.Exists(Path.Combine(javaHome, "lib", "rt.jar")))
            checks.Add("PASS: Java 8 rt.jar present.");
        else
            checks.Add("WARN: No release file or classic Java 8 markers found.");

        var regHome = _registry.DetectInstalledJdk();
        if (!string.IsNullOrWhiteSpace(regHome))
            checks.Add($"PASS: Registry JAVA_HOME: {regHome}");
        else
            checks.Add("WARN: JDK not yet registered under JavaSoft registry keys.");

        string? versionOutput = null;
        if (File.Exists(javaExe))
        {
            try
            {
                var psi = new ProcessStartInfo(javaExe, "-version")
                {
                    RedirectStandardError = true,
                    UseShellExecute       = false,
                    CreateNoWindow        = true
                };
                using var proc = Process.Start(psi)!;
                versionOutput = proc.StandardError.ReadToEnd();
                proc.WaitForExit(10_000);
                if (!string.IsNullOrWhiteSpace(versionOutput))
                    checks.Add($"PASS: java -version: {versionOutput.Trim().Replace('\n', ' ')}");
                else
                    checks.Add("FAIL: java -version produced no output.");
            }
            catch (Exception ex)
            {
                checks.Add($"FAIL: java -version threw: {ex.Message}");
            }
        }

        var passed = checks.All(c => c.StartsWith("PASS", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("WARN", StringComparison.OrdinalIgnoreCase))
            && checks.Any(c => c.StartsWith("PASS", StringComparison.OrdinalIgnoreCase)
                && c.Contains("java.exe", StringComparison.OrdinalIgnoreCase));

        return new JdkInstallValidationResult
        {
            Passed          = passed,
            JavaHome        = javaHome,
            JavaVersionOutput = versionOutput,
            Checks          = checks
        };
    }

    private static string? ResolveInstalledJavaHome(string targetJavaHome, string? installRoot)
    {
        if (Directory.Exists(targetJavaHome) && File.Exists(Path.Combine(targetJavaHome, "bin", "java.exe")))
            return targetJavaHome;

        if (!string.IsNullOrWhiteSpace(installRoot) && Directory.Exists(installRoot))
        {
            var nested = Directory.GetDirectories(installRoot, "jdk*", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetDirectories(installRoot, "jdk-*", SearchOption.TopDirectoryOnly))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "bin", "java.exe")));
            if (nested is not null) return nested;

            if (File.Exists(Path.Combine(installRoot, "bin", "java.exe")))
                return installRoot;
        }

        // Oracle may install sibling folder with slightly different patch
        var parent = Path.GetDirectoryName(targetJavaHome.TrimEnd('\\'));
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            var prefix = Path.GetFileName(targetJavaHome);
            var match = Directory.GetDirectories(parent)
                .FirstOrDefault(d =>
                {
                    var name = Path.GetFileName(d);
                    return name.StartsWith("jdk", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(Path.Combine(d, "bin", "java.exe"));
                });
            if (match is not null) return match;
        }

        return null;
    }
}

public sealed class JdkInstallValidationResult
{
    public bool Passed { get; init; }
    public string? JavaHome { get; init; }
    public string? JavaVersionOutput { get; init; }
    public IReadOnlyList<string> Checks { get; init; } = [];
}
