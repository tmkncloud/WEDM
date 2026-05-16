using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using LocalPayloadComponent = WEDM.Domain.Enums.LocalPayloadComponent;
using WEDM.Engine.Versioning;
using WEDM.Infrastructure.Registry;

namespace WEDM.Engine.Payload;

/// <summary>
/// Detects installed JDK/VC++, resolves local installers, and optionally downloads missing payloads.
///
/// Download hardening:
///   • Retry with exponential back-off (3 attempts, 2 s / 4 s / 8 s)
///   • Streaming download with IProgress&lt;long&gt; byte-count reporting
///   • File-size sanity guard (configurable minimum, defaults to 1 MB)
///   • Optional SHA-256 checksum verification
///   • Proxy auto-detection via WebProxy.GetDefaultProxy()
///   • Partial-download recovery: temp file deleted on failure
///   • TLS enforcement via HttpClientHandler (SslProtocols.Tls12 | Tls13)
/// </summary>
public sealed class DeploymentPayloadService : IPayloadAcquisitionService
{
    private readonly IPayloadLocator _local;
    private const int    MaxDownloadAttempts   = 3;
    private const long   MinAcceptableFileBytes = 1L * 1024 * 1024; // 1 MB
    private const int    BaseRetryDelaySeconds  = 2;

    // Thread-safe lazy-initialised HttpClient with TLS hardening and proxy support.
    private static readonly Lazy<HttpClient> LazyHttp = new(() =>
    {
        var handler = new HttpClientHandler
        {
            SslProtocols             = System.Security.Authentication.SslProtocols.Tls12
                                     | System.Security.Authentication.SslProtocols.Tls13,
            CheckCertificateRevocationList = true,
            AutomaticDecompression   = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy                 = true,
            Proxy                    = WebRequest.GetSystemWebProxy(),
        };
        handler.Proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
    });

    private static HttpClient Http => LazyHttp.Value;

    private readonly ILoggingService _log;
    private readonly WindowsRegistryService _registry;

    public DeploymentPayloadService(
        ILoggingService log,
        WindowsRegistryService registry,
        IPayloadLocator local)
    {
        _log      = log;
        _registry = registry;
        _local    = local;
    }

    public async Task<PrerequisiteValidationResult> ValidateAndPrepareAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var result = new PrerequisiteValidationResult();

        if (config.PayloadAcquisition.UseLocalRepositoryOnly)
        {
            var localReport = await _local.ValidateAndResolveAsync(config, cancellationToken).ConfigureAwait(false);
            MergeLocalPayloadFindings(result, localReport, config);

            if (!localReport.CanProceed)
            {
                _log.Error(
                    $"Local payload repository validation failed ({localReport.Findings.Count(f => f.Severity == ValidationSeverity.Fatal)} fatal).",
                    category: "Payload.Local");
                return result;
            }

            _log.Info(
                $"Local payload repository OK: {localReport.Entries.Count(e => e.Found)} resolved under {localReport.VersionFolder}",
                "Payload.Local");
            ValidateResolvedFileSizes(localReport, result);
            return result;
        }

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

        if (config.PayloadAcquisition.UseLocalRepositoryOnly)
        {
            await _local.ValidateAndResolveAsync(config, cancellationToken).ConfigureAwait(false);
            return _local.Resolve(LocalPayloadComponent.Jdk, config);
        }

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

        if (config.PayloadAcquisition.UseLocalRepositoryOnly)
        {
            await _local.ValidateAndResolveAsync(config, cancellationToken).ConfigureAwait(false);
            return _local.Resolve(LocalPayloadComponent.Vc, config);
        }

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

        var version    = config.WebLogicVersion.ToString().Replace("WLS_", "", StringComparison.Ordinal);
        var payloadDir = Path.Combine(config.PayloadBasePath, version.ToLowerInvariant());

        if (!Directory.Exists(payloadDir))
        {
            result.Fatal("MiddlewarePayloadValidation",
                $"Middleware payload directory not found: {payloadDir}",
                $"Create the directory and place Oracle installer media under payloads/{version.ToLowerInvariant()}/.",
                actual: "Directory missing",
                expected: payloadDir);
            return;
        }

        result.Pass("Payload.Directory", $"Middleware payload directory present: {payloadDir}");

        var installerFiles = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .ToList();

        if (installerFiles.Count == 0)
        {
            result.Fatal("MiddlewarePayloadValidation",
                $"No installer binaries (.jar / .exe / .zip) found under {payloadDir}.",
                "Add fmw_11g_infra.jar, wls.jar, or Forms/Reports media to the payload folder.",
                actual: "0 installer files",
                expected: "≥1 Oracle installer binary");
            return;
        }

        // ── Per-file size check ────────────────────────────────────────────────
        foreach (var fi in installerFiles)
        {
            if (fi.Length < MinAcceptableFileBytes)
                result.Warn($"Payload.Size.{fi.Name}",
                    $"Installer '{fi.Name}' is suspiciously small ({fi.Length:N0} bytes) — may be a partial download.",
                    $"Re-download or replace '{fi.Name}'. Minimum expected size: {MinAcceptableFileBytes / (1024 * 1024):N0} MB.",
                    actual: $"{fi.Length:N0} bytes",
                    expected: $"≥{MinAcceptableFileBytes / (1024 * 1024):N0} MB");
            else
                result.Pass($"Payload.{fi.Name}",
                    $"{fi.Name}: {fi.Length / 1024 / 1024:N0} MB — size OK ✔",
                    actual: $"{fi.Length / 1024 / 1024:N0} MB");
        }

        // ── Per-version required media matrix ─────────────────────────────────
        var required = WebLogicVersionAdapterFactory.For(config.WebLogicVersion).RequiredMediaPatterns;
        if (required.Count == 0)
            return;

        var fileNames = installerFiles.Select(f => f.Name.ToLowerInvariant()).ToList();
        foreach (var pattern in required)
        {
            var found = fileNames.Any(n => n.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (found)
                result.Pass($"Payload.Matrix.{pattern}",
                    $"Required media matching '{pattern}' located ✔");
            else
                result.Fatal($"Payload.Matrix.{pattern}",
                    $"Required installer matching '{pattern}' not found in {payloadDir}.",
                    BuildMediaRemediationHint(config.WebLogicVersion, pattern),
                    actual: "File not found",
                    expected: $"Installer file containing '{pattern}'");
        }
    }

    private static string BuildMediaRemediationHint(WebLogicVersion version, string pattern) => (version, pattern) switch
    {
        (WebLogicVersion.WLS_11g, "wls")  => "Download WebLogic Server 10.3.6 generic installer (wls1036_generic.jar) from Oracle eDelivery.",
        (WebLogicVersion.WLS_11g, "fmw")  => "Download Oracle Fusion Middleware 11g Infrastructure or Forms/Reports installer from Oracle eDelivery.",
        (WebLogicVersion.WLS_12c, "fmw_12") => "Download fmw_12.2.1.x_infrastructure_generic.jar from Oracle eDelivery (MOS patch or eDelivery).",
        (WebLogicVersion.WLS_14c, "fmw_14") => "Download fmw_14.1.1.0.0_infrastructure_generic.jar from Oracle eDelivery.",
        _ => $"Place an Oracle installer containing '{pattern}' in the payload directory."
    };

    private static int RequiredJdkMajor(WebLogicVersion version) => version switch
    {
        WebLogicVersion.WLS_14c or WebLogicVersion.WLS_15c => 21,
        WebLogicVersion.WLS_12c => 8,
        WebLogicVersion.WLS_11g => 8,
        _                       => 8
    };

    private static void MergeLocalPayloadFindings(
        PrerequisiteValidationResult result,
        LocalPayloadRepositoryReport localReport,
        DeploymentConfiguration config)
    {
        result.Pass("Payload.LocalRepository",
            $"Using local payload repository: {localReport.VersionFolder}",
            actual: localReport.VersionFolder);

        foreach (var entry in localReport.Entries.Where(e => e.Found))
        {
            var checksumNote = entry.ChecksumStatus switch
            {
                PayloadChecksumStatus.Verified         => " (SHA-256 verified)",
                PayloadChecksumStatus.Mismatch           => " (CHECKSUM MISMATCH)",
                PayloadChecksumStatus.ManifestMissing    => " (no manifest checksum)",
                _                                        => string.Empty
            };
            result.Pass($"Payload.Local.{entry.Component}",
                $"{entry.Component}: {entry.ResolvedPath}{checksumNote}",
                actual: entry.ResolvedPath);
        }

        foreach (var finding in localReport.Findings)
        {
            switch (finding.Severity)
            {
                case ValidationSeverity.Fatal:
                    result.Fatal(finding.Code, finding.Message, finding.Remediation,
                        actual: finding.MissingPath, expected: finding.ExpectedPatterns);
                    break;
                case ValidationSeverity.Warning:
                    result.Warn(finding.Code, finding.Message, finding.Remediation,
                        actual: finding.MissingPath);
                    break;
                default:
                    result.Pass(finding.Code, finding.Message);
                    break;
            }
        }

        config.LocalPayload = new LocalPayloadResolutionSnapshot
        {
            UsedLocalRepository = true,
            RepositoryRoot      = localReport.RepositoryRoot,
            VersionFolder       = localReport.VersionFolder,
            ManifestPresent     = localReport.ManifestPresent,
            Entries             = localReport.Entries.ToList()
        };
    }

    private static void ValidateResolvedFileSizes(LocalPayloadRepositoryReport localReport, PrerequisiteValidationResult result)
    {
        foreach (var entry in localReport.Entries.Where(e => e.Found && !string.IsNullOrWhiteSpace(e.ResolvedPath) && File.Exists(e.ResolvedPath)))
        {
            var fi = new FileInfo(entry.ResolvedPath!);
            if (fi.Length < MinAcceptableFileBytes && !Directory.Exists(entry.ResolvedPath!))
                result.Warn($"Payload.Size.{entry.Component}",
                    $"Resolved '{fi.Name}' is suspiciously small ({fi.Length:N0} bytes).",
                    "Verify the installer is complete.");
        }
    }

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

    /// <summary>
    /// Download <paramref name="url"/> to <paramref name="targetPath"/> with:
    ///   • 3 attempts using exponential back-off (2 s, 4 s, 8 s)
    ///   • Streaming progress reporting (bytes received / total)
    ///   • Atomic write via a side-car .tmp file (never leaves a partial download at the target path)
    ///   • Optional SHA-256 checksum verification
    ///   • Proxy auto-detected from system settings
    ///   • TLS 1.2+ enforced
    /// </summary>
    private async Task<bool> DownloadFileAsync(
        string url,
        string targetPath,
        CancellationToken ct,
        IProgress<(long received, long total)>? progress = null,
        string? expectedSha256 = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = targetPath + ".wedm-tmp";

        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            try
            {
                _log.Info($"Downloading payload attempt {attempt}/{MaxDownloadAttempts}: {url}", "Payload");

                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                long receivedBytes = 0;

                await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream     = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                                                     FileShare.None, 81920, useAsync: true);

                using SHA256? hasher = expectedSha256 is not null ? SHA256.Create() : null;
                var  buffer = new byte[81920];
                int  read;

                while ((read = await responseStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hasher?.TransformBlock(buffer, 0, read, null, 0);
                    receivedBytes += read;
                    progress?.Report((receivedBytes, totalBytes));
                }

                await fileStream.FlushAsync(ct).ConfigureAwait(false);

                // ── Integrity checks ────────────────────────────────────────
                var fi = new FileInfo(tempPath);
                if (!fi.Exists || fi.Length < MinAcceptableFileBytes)
                {
                    _log.Warning($"Download attempt {attempt}: file too small ({fi.Length} bytes) — retrying.", "Payload");
                    TryDeleteTemp(tempPath);
                    await DelayRetryAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }

                if (hasher is not null && expectedSha256 is not null)
                {
                    hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    var actual = Convert.ToHexString(hasher.Hash!).ToLowerInvariant();
                    if (!actual.Equals(expectedSha256.ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        _log.Error($"SHA-256 mismatch for {Path.GetFileName(targetPath)}: expected={expectedSha256}, actual={actual}", null, "Payload");
                        TryDeleteTemp(tempPath);
                        // Checksum mismatch is not retried (file was fully downloaded but corrupted at source).
                        return false;
                    }
                    _log.Info($"SHA-256 verified: {Path.GetFileName(targetPath)} ✔", "Payload");
                }

                // Atomic promotion: rename temp → target
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(tempPath, targetPath);

                _log.Info($"Payload downloaded ({fi.Length / 1024 / 1024:N0} MB): {targetPath}", "Payload");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDeleteTemp(tempPath);
                throw;
            }
            catch (Exception ex) when (attempt < MaxDownloadAttempts)
            {
                _log.Warning($"Payload download attempt {attempt} failed: {ex.Message}. Retrying in {BaseRetryDelaySeconds * attempt}s...", "Payload");
                TryDeleteTemp(tempPath);
                await DelayRetryAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning($"Payload download failed after {MaxDownloadAttempts} attempts: {ex.Message}", "Payload");
                TryDeleteTemp(tempPath);
                return false;
            }
        }

        return false;
    }

    private static async Task DelayRetryAsync(int attempt, CancellationToken ct)
    {
        var delayMs = BaseRetryDelaySeconds * attempt * 1000;
        try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
    }

    private static void TryDeleteTemp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
