using System.Text.Json;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Registry;

namespace WEDM.Engine.Payload;

/// <summary>
/// Detects installed JDK/VC++, resolves local installers, and optionally downloads missing payloads.
/// </summary>
public sealed class DeploymentPayloadService : IPayloadAcquisitionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly ILoggingService _log;
    private readonly WindowsRegistryService _registry;

    public DeploymentPayloadService(ILoggingService log, WindowsRegistryService registry)
    {
        _log       = log;
        _registry  = registry;
    }

    public async Task<PrerequisiteValidationResult> ValidateAndPrepareAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var result = new PrerequisiteValidationResult();
        Directory.CreateDirectory(config.PayloadAcquisition.CacheDirectory);

        if (config.Components.HasFlag(InstallationComponents.JDK))
        {
            if (TryDetectCompatibleJdk(config, out var javaHome))
            {
                config.Java.JavaHome = javaHome!;
                result.Pass("Payload.JDK", $"JDK already installed at '{javaHome}' — install step will be skipped.");
            }
            else
            {
                var jdk = await EnsureJdkInstallerAsync(config, cancellationToken).ConfigureAwait(false);
                if (jdk.Success)
                    result.Pass("Payload.JDK", jdk.Message);
                else
                    result.Fatal("Payload.JDK", jdk.Message,
                        config.PayloadAcquisition.AutoDownloadMissing
                            ? "Check network connectivity and payload cache permissions."
                            : "Set jdkInstallerPath or enable auto-download in payloadAcquisition.",
                        actual: "Missing",
                        expected: $"JDK {RequiredJdkMajor(config.WebLogicVersion)} installer (Temurin/OpenJDK)");
            }
        }

        if (config.Components.HasFlag(InstallationComponents.VCRedist))
        {
            if (IsVcRedistInstalled())
                result.Pass("Payload.VCRedist", "Visual C++ Redistributable already installed — install step will be skipped.");
            else
            {
                var vc = await EnsureVcRedistInstallerAsync(config, cancellationToken).ConfigureAwait(false);
                if (vc.Success)
                    result.Pass("Payload.VCRedist", vc.Message);
                else
                    result.Fatal("Payload.VCRedist", vc.Message,
                        config.PayloadAcquisition.AutoDownloadMissing
                            ? "Check network connectivity and payload cache permissions."
                            : "Set vcRedistX64InstallerPath or enable auto-download.");
            }
        }

        ValidateMiddlewarePayloads(config, result);
        return result;
    }

    public bool TryDetectCompatibleJdk(DeploymentConfiguration config, out string? javaHome)
    {
        javaHome = null;
        if (!string.IsNullOrWhiteSpace(config.Java.JavaHome) && Directory.Exists(config.Java.JavaHome))
        {
            javaHome = config.Java.JavaHome;
            return IsCompatibleJdk(javaHome, config.WebLogicVersion);
        }

        var detected = _registry.DetectInstalledJdk();
        if (detected is null || !IsCompatibleJdk(detected, config.WebLogicVersion))
            return false;

        javaHome = detected;
        config.Java.JavaHome = detected;
        return true;
    }

    public bool IsVcRedistInstalled() => _registry.IsVcRedistInstalled();

    public async Task<PayloadResolutionResult> EnsureJdkInstallerAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Components.HasFlag(InstallationComponents.JDK))
            return new() { Status = PayloadResolutionStatus.NotRequired, Message = "JDK component not selected." };

        if (config.PayloadAcquisition.SkipInstallWhenPresent && TryDetectCompatibleJdk(config, out _))
            return new() { Status = PayloadResolutionStatus.AlreadyInstalled, Message = "JDK already installed — skipped." };

        if (!string.IsNullOrWhiteSpace(config.JdkInstallerPath) && File.Exists(config.JdkInstallerPath))
        {
            return new()
            {
                Status        = PayloadResolutionStatus.ResolvedExisting,
                InstallerPath = config.JdkInstallerPath,
                Message       = $"Using configured JDK installer: {config.JdkInstallerPath}"
            };
        }

        var cached = FindCachedInstaller(config.PayloadAcquisition.CacheDirectory, "jdk", "*.msi", "*.exe");
        if (cached is not null)
        {
            config.JdkInstallerPath = cached;
            return new()
            {
                Status        = PayloadResolutionStatus.ResolvedExisting,
                InstallerPath = cached,
                Message       = $"Using cached JDK installer: {cached}"
            };
        }

        if (!config.PayloadAcquisition.AutoDownloadMissing)
            return new() { Status = PayloadResolutionStatus.Failed, Message = "JDK installer not found and auto-download is disabled." };

        var major = RequiredJdkMajor(config.WebLogicVersion);
        var fileName = $"wedm-jdk{major}-windows-x64.msi";
        var target = Path.Combine(config.PayloadAcquisition.CacheDirectory, fileName);
        var url = await ResolveTemurinMsiUrlAsync(major, cancellationToken).ConfigureAwait(false);
        if (url is null)
            return new() { Status = PayloadResolutionStatus.Failed, Message = $"Could not resolve JDK {major} download URL (Temurin/Adoptium)." };

        var ok = await DownloadFileAsync(url, target, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return new() { Status = PayloadResolutionStatus.Failed, Message = $"JDK download failed: {url}" };

        config.JdkInstallerPath = target;
        _log.Info($"JDK installer downloaded: {target}", "Payload");
        return new()
        {
            Status        = PayloadResolutionStatus.Downloaded,
            InstallerPath = target,
            Message       = $"Downloaded JDK installer to {target}"
        };
    }

    public async Task<PayloadResolutionResult> EnsureVcRedistInstallerAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Components.HasFlag(InstallationComponents.VCRedist))
            return new() { Status = PayloadResolutionStatus.NotRequired, Message = "VC++ component not selected." };

        if (config.PayloadAcquisition.SkipInstallWhenPresent && IsVcRedistInstalled())
            return new() { Status = PayloadResolutionStatus.AlreadyInstalled, Message = "VC++ already installed — skipped." };

        if (!string.IsNullOrWhiteSpace(config.VcRedistX64InstallerPath) && File.Exists(config.VcRedistX64InstallerPath))
        {
            return new()
            {
                Status        = PayloadResolutionStatus.ResolvedExisting,
                InstallerPath = config.VcRedistX64InstallerPath,
                Message       = $"Using configured VC++ installer: {config.VcRedistX64InstallerPath}"
            };
        }

        var cached = FindCachedInstaller(config.PayloadAcquisition.CacheDirectory, "vc", "vc_redist*.exe", "*.exe");
        if (cached is not null)
        {
            config.VcRedistX64InstallerPath = cached;
            return new()
            {
                Status        = PayloadResolutionStatus.ResolvedExisting,
                InstallerPath = cached,
                Message       = $"Using cached VC++ installer: {cached}"
            };
        }

        if (!config.PayloadAcquisition.AutoDownloadMissing)
            return new() { Status = PayloadResolutionStatus.Failed, Message = "VC++ installer not found and auto-download is disabled." };

        const string url = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
        var target = Path.Combine(config.PayloadAcquisition.CacheDirectory, "vc_redist.x64.exe");
        var ok = await DownloadFileAsync(url, target, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return new() { Status = PayloadResolutionStatus.Failed, Message = $"VC++ download failed from {url}" };

        config.VcRedistX64InstallerPath = target;
        _log.Info($"VC++ installer downloaded: {target}", "Payload");
        return new()
        {
            Status        = PayloadResolutionStatus.Downloaded,
            InstallerPath = target,
            Message       = $"Downloaded VC++ redistributable to {target}"
        };
    }

    private static void ValidateMiddlewarePayloads(DeploymentConfiguration config, PrerequisiteValidationResult result)
    {
        var needsMw = config.Components.HasFlag(InstallationComponents.Infrastructure)
            || config.Components.HasFlag(InstallationComponents.WebLogicServer)
            || config.Components.HasFlag(InstallationComponents.FormsReports);

        if (!needsMw) return;

        var version = config.WebLogicVersion.ToString().Replace("WLS_", "", StringComparison.Ordinal);
        var payloadDir = Path.Combine(config.PayloadBasePath, version.ToLowerInvariant());
        if (!Directory.Exists(payloadDir))
        {
            result.Fatal("MiddlewarePayloadValidation",
                $"Middleware payload directory not found: {payloadDir}",
                $"Place Oracle installer media in payloads/{version.ToLowerInvariant()}/ before deployment.",
                actual: "Missing",
                expected: payloadDir);
            return;
        }

        result.Pass("Payload.Middleware", $"Middleware payload directory: {payloadDir}");
        var files = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0)
        {
            result.Fatal("MiddlewarePayloadValidation",
                $"No installer binaries found under {payloadDir}",
                "Add fmw_11g_infra.jar, wls.jar, or Forms/Reports media to the payload folder.",
                actual: "Missing",
                expected: "Installer JAR/EXE/ZIP under payload directory");
            return;
        }

        foreach (var file in files)
        {
            var fi = new FileInfo(file);
            if (fi.Length < 1024)
                result.Warn($"Payload.{fi.Name}", $"{fi.Name} appears too small ({fi.Length} bytes).");
            else
                result.Pass($"Payload.{fi.Name}", $"{fi.Name}: {fi.Length / 1024 / 1024:N0} MB");
        }
    }

    private static int RequiredJdkMajor(WebLogicVersion version) => version switch
    {
        WebLogicVersion.WLS_14c => 21,
        WebLogicVersion.WLS_12c => 8,
        WebLogicVersion.WLS_11g => 8,
        _                       => 8
    };

    private static bool IsCompatibleJdk(string javaHome, WebLogicVersion version)
    {
        var javaExe = Path.Combine(javaHome, "bin", "java.exe");
        if (!File.Exists(javaExe)) return false;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(javaExe, "-version")
            {
                RedirectStandardError = true,
                UseShellExecute       = false,
                CreateNoWindow        = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var line = proc.StandardError.ReadLine() ?? string.Empty;
            proc.WaitForExit(5000);
            var match = System.Text.RegularExpressions.Regex.Match(line, @"""(\d+)\.?(\d*)");
            if (!match.Success) return false;
            var major = int.Parse(match.Groups[1].Value);
            if (major == 1) major = int.Parse(match.Groups[2].Value);
            var required = RequiredJdkMajor(version);
            return major == required || (version == WebLogicVersion.WLS_11g && major is 7 or 8);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindCachedInstaller(string cacheDir, string prefix, params string[] patterns)
    {
        if (!Directory.Exists(cacheDir)) return null;
        foreach (var pattern in patterns)
        {
            var hit = Directory.GetFiles(cacheDir, pattern, SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => Path.GetFileName(f).Contains(prefix, StringComparison.OrdinalIgnoreCase)
                                  || pattern.Contains(prefix, StringComparison.OrdinalIgnoreCase));
            if (hit is not null && new FileInfo(hit).Length > 1024 * 1024)
                return hit;
        }
        return Directory.GetFiles(cacheDir, patterns[0], SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => new FileInfo(f).Length > 1024 * 1024);
    }

    private static async Task<string?> ResolveTemurinMsiUrlAsync(int jdkMajor, CancellationToken ct)
    {
        try
        {
            var api = $"https://api.adoptium.net/v3/assets/latest/{jdkMajor}/hotspot?os=windows&architecture=x64&image_type=jdk&package_type=msi";
            using var doc = await JsonDocument.ParseAsync(await Http.GetStreamAsync(api, ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;
            var binary = doc.RootElement[0].GetProperty("binary").GetProperty("msi");
            return binary.GetProperty("link").GetString();
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> DownloadFileAsync(string url, string targetPath, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
            await using var file = File.Create(targetPath);
            await stream.CopyToAsync(file, ct).ConfigureAwait(false);
            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length < 1024)
                return false;
            _log.Info($"Downloaded payload ({new FileInfo(targetPath).Length / 1024 / 1024:N0} MB): {targetPath}", "Payload");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"Payload download failed: {ex.Message}", "Payload");
            return false;
        }
    }
}
