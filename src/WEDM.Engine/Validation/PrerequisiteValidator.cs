using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WEDM.Domain.Enums;
using WEDM.Engine.Decommissioning;
using WEDM.Engine.Opatch;
using WEDM.Engine.Versioning;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Validation;

/// <summary>
/// Full prerequisite validation engine for Oracle WebLogic deployments.
/// Checks are modular, independently executable, and report structured findings
/// with remediation guidance for each failure.
///
/// Enterprise requirements validated:
///  - OS version (Windows Server 2016+ required for WLS 12c/14c)
///  - RAM (minimum 8 GB for Forms+Reports; 4 GB WLS only)
///  - CPU (minimum 2 cores)
///  - Disk space per drive (MW home, domain, logs)
///  - TCP port availability (AdminServer, Node Manager, managed servers, OHS)
///  - Administrator privileges (required for registry and service operations)
///  - JDK version compatibility with WebLogic version
///  - VC++ Redistributable presence
///  - Payload binary integrity (file existence + size check)
///  - Database connectivity (if RCU required)
/// </summary>
public sealed class PrerequisiteValidator : IValidationEngine
{
    private readonly ILoggingService _log;
    private readonly Infrastructure.Registry.WindowsRegistryService _registry;
    private readonly IPayloadAcquisitionService _payloads;
    private readonly IDeployOracleConflictDetector? _oracleConflicts;

    public PrerequisiteValidator(
        ILoggingService log,
        Infrastructure.Registry.WindowsRegistryService registry,
        IPayloadAcquisitionService payloads,
        IDeployOracleConflictDetector? oracleConflicts = null)
    {
        _log             = log;
        _registry        = registry;
        _payloads        = payloads;
        _oracleConflicts = oracleConflicts;
    }

    // ── Full validation suite ─────────────────────────────────────────────────

    public async Task<PrerequisiteValidationResult> ValidateAllAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        _log.Info("Starting full prerequisite validation...", "Validation");
        var result = new PrerequisiteValidationResult();

        var tasks = new List<Task<PrerequisiteValidationResult>>
        {
            ValidatePrivilegesAsync(cancellationToken),
            ValidateOperatingSystemAsync(cancellationToken),
            ValidateHardwareAsync(config, cancellationToken),
            ValidateDiskSpaceAsync(config, cancellationToken),
            ValidatePortsAsync(config, cancellationToken),
            ValidateJavaAsync(config, cancellationToken),
            ValidateVcRedistAsync(cancellationToken),
            ValidatePayloadIntegrityAsync(config, cancellationToken),
        };

        if (config.Database.RunRcu)
            tasks.Add(ValidateDatabaseConnectivityAsync(config, cancellationToken));

        if (_oracleConflicts is not null)
            tasks.Add(ValidateOracleLifecycleConflictsAsync(config, cancellationToken));

        var results = await Task.WhenAll(tasks);
        foreach (var r in results) result.AddRange(r.Findings);

        _log.Info($"Validation complete: {result.PassedCount}/{result.TotalChecks} passed, " +
                  $"{result.FailedCount} errors, {result.WarningCount} warnings", "Validation");

        if (!result.CanProceed)
            PrerequisiteValidationReporter.LogBlockingFindings(_log, result);

        return result;
    }

    // ── Privileges ────────────────────────────────────────────────────────────

    public Task<PrerequisiteValidationResult> ValidatePrivilegesAsync(CancellationToken ct = default)
    {
        var result = new PrerequisiteValidationResult();
        var isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        if (isAdmin)
            result.Pass("AdministratorPrivileges", "Running as Administrator ✔");
        else
            result.Fatal("AdministratorPrivileges",
                "WEDM must run as Administrator for registry edits and service installation.",
                "Right-click WEDM.exe → Run as administrator.");

        return Task.FromResult(result);
    }

    // ── Operating System ──────────────────────────────────────────────────────

    public Task<PrerequisiteValidationResult> ValidateOperatingSystemAsync(CancellationToken ct = default)
    {
        var result = new PrerequisiteValidationResult();
        var os = Environment.OSVersion;

        // Must be Windows
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            result.Fatal("OperatingSystem", "WEDM Windows installer requires a Windows OS.",
                "Use the Linux automation module for Linux deployments.");
            return Task.FromResult(result);
        }

        // Windows Server 2016+ (version 10.0.14393+) for WLS 12c/14c
        if (os.Version >= new Version(10, 0, 14393))
            result.Pass("OSVersion", $"OS: Windows {os.Version} — Supported ✔", os.Version.ToString());
        else if (os.Version >= new Version(6, 1))
            result.Warn("OSVersion",
                $"Windows {os.Version} is below the recommended Windows Server 2016 for WLS 12c/14c.",
                "Upgrade to Windows Server 2016 or later for production deployments.",
                os.Version.ToString(), "10.0+");
        else
            result.Fatal("OSVersion", $"Windows {os.Version} is not supported.",
                "Minimum: Windows Server 2012 R2 (6.3). Recommended: Windows Server 2019/2022.");

        // 64-bit
        if (Environment.Is64BitOperatingSystem)
            result.Pass("OSArchitecture", "64-bit OS detected ✔");
        else
            result.Fatal("OSArchitecture", "64-bit Windows is required for Oracle WebLogic.",
                "Oracle WebLogic Server does not support 32-bit Windows.");

        return Task.FromResult(result);
    }

    // ── Hardware ──────────────────────────────────────────────────────────────

    public Task<PrerequisiteValidationResult> ValidateHardwareAsync(
        DeploymentConfiguration config, CancellationToken ct = default)
    {
        var result = new PrerequisiteValidationResult();
        var adapter = WebLogicVersionAdapterFactory.For(config.WebLogicVersion);

        // RAM
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                var totalKb = Convert.ToInt64(obj["TotalVisibleMemorySize"]);
                var totalMb = totalKb / 1024;
                if (totalMb >= adapter.MinRamMb)
                    result.Pass("RAM", $"RAM: {totalMb:N0} MB — Sufficient ✔", totalMb);
                else
                    result.Fail("RAM",
                        $"Insufficient RAM: {totalMb:N0} MB detected, minimum {adapter.MinRamMb:N0} MB required.",
                        $"Add at least {adapter.MinRamMb - totalMb:N0} MB of RAM.",
                        totalMb, adapter.MinRamMb);
            }
        }
        catch (Exception ex)
        {
            result.Warn("RAM", $"Could not query RAM via WMI: {ex.Message}",
                "Manually verify at least 8 GB RAM is installed.");
        }

        // CPU
        var coreCount = Environment.ProcessorCount;
        if (coreCount >= adapter.MinCpuCores)
            result.Pass("CPU", $"CPU: {coreCount} logical cores ✔", coreCount);
        else
            result.Warn("CPU",
                $"Only {coreCount} CPU core(s) detected; {adapter.MinCpuCores} recommended for {config.WebLogicVersion}.",
                "WebLogic performance may be degraded. Add CPU resources if possible.",
                coreCount, adapter.MinCpuCores);

        return Task.FromResult(result);
    }

    // ── Disk Space ────────────────────────────────────────────────────────────

    public Task<PrerequisiteValidationResult> ValidateDiskSpaceAsync(
        DeploymentConfiguration config, CancellationToken ct = default)
    {
        var result = new PrerequisiteValidationResult();
        var adapter = WebLogicVersionAdapterFactory.For(config.WebLogicVersion);

        var checkPaths = new Dictionary<string, long>
        {
            { config.Paths.OracleRoot,       adapter.MinDiskGb * 1024L * 1024L * 1024L },
            { config.Paths.TempDirectory,    2L * 1024 * 1024 * 1024 },   // 2 GB temp
            { config.Paths.LogDirectory,     512L * 1024 * 1024 },         // 512 MB logs
        };

        foreach (var (path, minBytes) in checkPaths)
        {
            try
            {
                var drive = Path.GetPathRoot(path) ?? "C:\\";
                var info  = new DriveInfo(drive);
                var availGb = info.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                var minGb   = minBytes / 1024.0 / 1024 / 1024;

                if (info.AvailableFreeSpace >= minBytes)
                    result.Pass($"DiskSpace.{drive}", $"Drive {drive}: {availGb:F1} GB free ✔", $"{availGb:F1} GB");
                else
                    result.Fail($"DiskSpace.{drive}",
                        $"Drive {drive}: Only {availGb:F1} GB free; {minGb:F1} GB required for {path}.",
                        $"Free at least {minGb:F1} GB on drive {drive}.",
                        $"{availGb:F1} GB", $"{minGb:F1} GB");
            }
            catch (Exception ex)
            {
                result.Warn($"DiskSpace.{path}", $"Cannot query disk space for '{path}': {ex.Message}");
            }
        }

        return Task.FromResult(result);
    }

    // ── Ports ─────────────────────────────────────────────────────────────────

    public async Task<PrerequisiteValidationResult> ValidatePortsAsync(
        DeploymentConfiguration config, CancellationToken ct = default)
    {
        var result = new PrerequisiteValidationResult();
        var portsToCheck = new List<(int Port, string Name)>
        {
            (config.Domain.AdminPort,              "AdminServer HTTP"),
            (config.Domain.AdminSslPort,           "AdminServer HTTPS"),
            (config.Domain.NodeManager.Port,       "NodeManager"),
        };

        foreach (var ms in config.Domain.ManagedServers)
        {
            portsToCheck.Add((ms.Port,    $"{ms.Name} HTTP"));
            portsToCheck.Add((ms.SslPort, $"{ms.Name} HTTPS"));
        }

        if (config.ConfigureFormsReports)
            portsToCheck.Add((config.Domain.FormsReports.OhsPort, "OHS/WebTier"));

        foreach (var (port, name) in portsToCheck)
        {
            if (port <= 0) continue;
            var inUse = await IsPortInUseAsync(port, ct);
            if (!inUse)
                result.Pass($"Port.{port}", $"Port {port} ({name}) is available ✔", port);
            else
                result.Fail($"Port.{port}",
                    $"Port {port} ({name}) is already in use.",
                    $"Stop the process using port {port} or change the port in configuration.",
                    port, "Available");
        }

        return result;
    }

    // ── JDK ──────────────────────────────────────────────────────────────────

    public Task<PrerequisiteValidationResult> ValidateJavaAsync(
        DeploymentConfiguration config, CancellationToken ct = default)
    {
        var result = new PrerequisiteValidationResult();
        var adapter = WebLogicVersionAdapterFactory.For(config.WebLogicVersion);
        var (minMajor, maxMajor) = ParseJdkMajorRange(adapter);

        // Try registry detection first
        var javaHome = _registry.DetectInstalledJdk() ?? config.Java.JavaHome;

        if (string.IsNullOrWhiteSpace(javaHome))
        {
            // JDK not installed — check if installer is available
            if (config.Components.HasFlag(Domain.Enums.InstallationComponents.JDK))
                result.Pass("JDK", "JDK not yet installed — will be installed automatically by WEDM ✔");
            else
                result.Fatal("JDKVersionValidation",
                    "JDK not found and JDK installation is disabled.",
                    "Enable JDK installation in the wizard, or install Temurin/OpenJDK manually and set JAVA_HOME.",
                    actual: "Not installed",
                    expected: $"JDK {minMajor}");
            return Task.FromResult(result);
        }

        // Detect version from java.exe
        var javaExe = Path.Combine(javaHome, "bin", "java.exe");
        if (!File.Exists(javaExe))
        {
            result.Fail("JDK", $"JAVA_HOME detected as '{javaHome}' but java.exe not found.",
                "Reinstall the JDK or update JAVA_HOME.");
            return Task.FromResult(result);
        }

        try
        {
            var psi = new ProcessStartInfo(javaExe, "-version")
                { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi)!;
            var versionLine = proc.StandardError.ReadLine() ?? string.Empty;
            proc.WaitForExit(5000);

            // Parse "java version \"1.8.0_202\"" or "openjdk version \"21.0.4\""
            var match = System.Text.RegularExpressions.Regex.Match(
                versionLine, @"""(\d+)\.?(\d*)");
            if (match.Success)
            {
                var major = int.Parse(match.Groups[1].Value);
                if (major == 1) major = int.Parse(match.Groups[2].Value); // 1.8 → 8

                var supported = adapter.SupportedJdkVersions.Select(ParseJdkMajorToken).ToHashSet();
                if (supported.Contains(major) || (major >= minMajor && major <= maxMajor))
                    result.Pass("JDK", $"JDK {major} at '{javaHome}' — compatible with {adapter.VersionLabel} ✔", major);
                else
                    result.Fail("JDK",
                        $"JDK {major} is not compatible with {adapter.VersionLabel} (supported: {string.Join(", ", adapter.SupportedJdkVersions)}).",
                        $"Install a supported JDK for {adapter.VersionLabel}.",
                        major, string.Join(",", adapter.SupportedJdkVersions));
            }
            else
            {
                result.Warn("JDK", $"Could not parse JDK version from: {versionLine}",
                    "Manually verify java.exe version.");
            }
        }
        catch (Exception ex)
        {
            result.Warn("JDK", $"JDK version check failed: {ex.Message}");
        }

        return Task.FromResult(result);
    }

    // ── VC++ Redistributable ──────────────────────────────────────────────────

    public Task<PrerequisiteValidationResult> ValidateVcRedistAsync(CancellationToken ct = default)
    {
        var result = new PrerequisiteValidationResult();
        if (_registry.IsVcRedistInstalled())
            result.Pass("VCRedist", "Visual C++ Redistributable (x64) is installed ✔");
        else
            result.Warn("VCRedist",
                "Visual C++ Redistributable not detected — required by Oracle Forms/WebUtil.",
                "WEDM will install VC++ Redistributable automatically.");
        return Task.FromResult(result);
    }

    // ── Payload integrity ─────────────────────────────────────────────────────

    public Task<PrerequisiteValidationResult> ValidatePayloadIntegrityAsync(
        DeploymentConfiguration config, CancellationToken ct = default)
        => _payloads.ValidateAndPrepareAsync(config, ct);

    // ── Database Connectivity ─────────────────────────────────────────────────

    public async Task<PrerequisiteValidationResult> ValidateDatabaseConnectivityAsync(
        DeploymentConfiguration config, CancellationToken ct = default)
    {
        var result = new PrerequisiteValidationResult();
        var db = config.Database;

        // TCP reachability check only (no Oracle driver dependency)
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connectTask = tcp.ConnectAsync(db.Host, db.Port, ct).AsTask();
            var timeout     = Task.Delay(5000, ct);
            var winner      = await Task.WhenAny(connectTask, timeout);

            if (winner == connectTask && tcp.Connected)
                result.Pass("Database.TCP", $"DB {db.Host}:{db.Port} is reachable ✔");
            else
                result.Fail("Database.TCP",
                    $"Cannot reach Oracle DB at {db.Host}:{db.Port} — connection timed out.",
                    "Verify Oracle listener is running. Check firewall rules for port 1521.",
                    $"{db.Host}:{db.Port}", "Reachable");
        }
        catch (Exception ex)
        {
            result.Fail("Database.TCP",
                $"DB TCP check failed: {ex.Message}",
                "Verify DB host/port settings and network connectivity.");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PrerequisiteValidationResult> ValidateForPatchingAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        _log.Info("Starting OPatch-oriented prerequisite validation...", "Validation.Patch");
        var result = PrerequisiteValidationResult.New(config.Id);

        result.Merge(await ValidatePrivilegesAsync(cancellationToken));
        result.Merge(await ValidateDiskSpaceAsync(config, cancellationToken));

        if (!Directory.Exists(config.Paths.MiddlewareHome))
            result.Fatal("Patch.OracleHome", $"Middleware / Oracle home not found: '{config.Paths.MiddlewareHome}'.");

        if (!Directory.Exists(config.Paths.OracleInventory))
            result.Fatal("Patch.Inventory", $"Oracle central inventory directory not found: '{config.Paths.OracleInventory}'.");
        else
        {
            var comps = Path.Combine(config.Paths.OracleInventory, "ContentsXML", "comps.xml");
            if (!File.Exists(comps))
                result.Warn("Patch.Inventory",
                    $"Expected central inventory metadata not found: '{comps}'. OPatch may still work if the inventory pointer is valid.");
        }

        var opatch = OpatchPaths.ResolveOpatchBat(config.Paths.MiddlewareHome, config.Patches.OpatchBatPathOverride);
        if (opatch is null || !File.Exists(opatch))
            result.Fatal("Patch.OPatch", $"OPatch not found under '{config.Paths.MiddlewareHome}'. Expected OPatch\\opatch.bat.");

        if (!config.Patches.Enabled)
            result.Warn("Patch.Disabled", "Patching is disabled in configuration.");

        if (string.IsNullOrWhiteSpace(config.Patches.PatchStagingDirectory))
            result.Fatal("Patch.Staging", "Patch staging directory is not configured.");
        else if (!Directory.Exists(config.Patches.PatchStagingDirectory))
            result.Fatal("Patch.Staging", $"Patch staging directory does not exist: '{config.Patches.PatchStagingDirectory}'.");

        return result;
    }

    private Task<PrerequisiteValidationResult> ValidateOracleLifecycleConflictsAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = PrerequisiteValidationResult.New(config.Id);

        if (_oracleConflicts is null)
            return Task.FromResult(result);

        var report = _oracleConflicts.DetectConflicts(config);

        foreach (var finding in report.Findings)
        {
            var remediation = finding.Remediation ?? string.Empty;
            switch (finding.Severity)
            {
                case OracleConflictSeverity.Informational:
                    result.Pass($"OracleLifecycle.{finding.Code}", finding.Message);
                    break;
                case OracleConflictSeverity.Blocking when !config.OracleLifecycle.ForceCleanInstall:
                    result.Fatal($"OracleLifecycle.{finding.Code}", finding.Message, remediation);
                    break;
                case OracleConflictSeverity.Blocking when config.OracleLifecycle.ForceCleanInstall:
                    result.Warn($"OracleLifecycle.{finding.Code}",
                        $"{finding.Message} (Force Clean Install enabled — proceeding with sanitization.)",
                        remediation);
                    break;
                case OracleConflictSeverity.Error:
                    result.Fatal($"OracleLifecycle.{finding.Code}", finding.Message, remediation);
                    break;
                default:
                    result.Warn($"OracleLifecycle.{finding.Code}", finding.Message, remediation);
                    break;
            }
        }

        if (report.SuggestDecommission)
        {
            result.Warn(
                "OracleLifecycle.SuggestDecommission",
                "Oracle state conflicts detected. Run Remove WebLogic Environment before redeploying.",
                "Select Remove WebLogic Environment from the operation screen to decommission and sanitize.");
        }

        return Task.FromResult(result);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<bool> IsPortInUseAsync(int port, CancellationToken ct)
    {
        await Task.CompletedTask;  // make async for future platform extension
        try
        {
            var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            return props.GetActiveTcpListeners().Any(ep => ep.Port == port);
        }
        catch { return false; }
    }

    private static (int MinMajor, int MaxMajor) ParseJdkMajorRange(Versioning.IWebLogicVersionAdapter adapter)
    {
        var min = ParseJdkMajorToken(adapter.MinJdkVersion);
        var max = ParseJdkMajorToken(adapter.MaxJdkVersion ?? adapter.MinJdkVersion);
        if (max < min) max = min;
        return (min, max);
    }

    private static int ParseJdkMajorToken(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 8;
        var parts = version.TrimStart('1', '.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        return int.TryParse(parts[0], out var major) ? major : 8;
    }
}
