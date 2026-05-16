using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Diagnostics;
using WEDM.Engine.ResponseFiles;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>
/// Executes Oracle WebLogic Server / Fusion Middleware Infrastructure silent installation.
///
/// Strategy:
///   1. Generate OUI response file from DeploymentConfiguration
///   2. Invoke java -jar <installer.jar> -silent -responseFile <rsp> as elevated process
///   3. Monitor oraInstall*.log for progress
///   4. Verify middleware home directories exist post-install
///   5. Set ORACLE_HOME registry key
///
/// Supports: WLS 11g (wls_generic.jar), WLS 12c/14c (infrastructure.jar / wls.jar)
/// </summary>
public sealed class InstallWebLogicStep : IStepExecutor
{
    private readonly IPowerShellExecutor     _ps;
    private readonly ILoggingService         _log;
    private readonly ResponseFileGenerator   _rspGen;

    public InstallWebLogicStep(
        IPowerShellExecutor ps,
        ILoggingService log,
        ResponseFileGenerator rspGen)
    {
        _ps     = ps;
        _log    = log;
        _rspGen = rspGen;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.Info("Generating WebLogic response file...", "Install.WLS");

        // Locate JDK
        var javaExe = FindJavaExe(config);
        if (javaExe is null)
            return StepExecutionResult.Fail("java.exe not found. JDK must be installed before WebLogic.");

        // Locate installer JAR
        var jarPath = ResolveInstallerJar(config);
        if (jarPath is null || !File.Exists(jarPath))
            return StepExecutionResult.Fail($"WebLogic installer JAR not found. Searched: {jarPath}");

        // Generate response file(s)
        var rspPath = _rspGen.GenerateWebLogicResponseFile(config);
        _log.Info($"Response file: {rspPath}", "Install.WLS");

        string? silentXmlPath = null;
        string? invPtrPath   = null;
        if (config.WebLogicVersion == Domain.Enums.WebLogicVersion.WLS_11g)
            silentXmlPath = _rspGen.GenerateWls11gSilentXml(config);
        else
            invPtrPath = GetInventoryPtr(config);

        var argList = BuildJavaArgumentList(config, jarPath, rspPath, silentXmlPath, invPtrPath);
        _log.Info($"Launching: {javaExe} {string.Join(" ", argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}", "Install.WLS");

        // Elevated PowerShell: pass string[] to -ArgumentList so OUI receives correct argv.
        var psArgList = string.Join(", ", argList.Select(a => $"'{EscapeForPsSingleQuotedString(a)}'"));
        var psCommand = $@"
$ErrorActionPreference = 'Continue'
$javaExe = '{EscapeForPsSingleQuotedString(javaExe)}'
$argList = @({psArgList})
$outLog = Join-Path $env:TEMP 'wedm_wls_out.txt'
$errLog = Join-Path $env:TEMP 'wedm_wls_err.txt'
$proc = Start-Process -FilePath $javaExe -ArgumentList $argList -Wait -PassThru -NoNewWindow `
    -RedirectStandardOutput $outLog -RedirectStandardError $errLog
$exitCode = $proc.ExitCode
Get-Content $outLog -ErrorAction SilentlyContinue | Write-Output
Get-Content $errLog -ErrorAction SilentlyContinue | Write-Warning
exit $exitCode
";
        var ouiTimeout = TimeSpan.FromMinutes(Math.Max(30, config.OuiInstallTimeoutMinutes));
        var result = await _ps.ExecuteCommandAsync(
            psCommand,
            workingDirectory: config.Paths.TempDirectory,
            runAsAdministrator: true,
            cancellationToken: cancellationToken,
            operationTimeout: ouiTimeout);

        sw.Stop();

        var logPath = OracleInstallLogScanner.FindLatestOuiStyleLog(
            config.Paths.OracleInventory,
            config.Paths.MiddlewareHome,
            config.Paths.TempDirectory);
        if (logPath is not null)
        {
            _log.Info($"Latest OUI / cfgtoollogs candidate: {logPath}", "Install.WLS");
            _log.Verbose(OracleInstallLogScanner.ReadLogTail(logPath), "Install.WLS.LogTail");
        }
        else
            _log.Warning("No OUI install log located yet (check inventory and cfgtoollogs after failure).", "Install.WLS");

        if (result.TimedOut)
            return StepExecutionResult.Fail($"WebLogic installer timed out after {ouiTimeout}. See log tail above.", -2);

        if (result.ExitCode != 0)
        {
            var errSummary = result.Errors.Length > 500 ? result.Errors[..500] + "…" : result.Errors;
            return StepExecutionResult.Fail(
                $"WebLogic installer exited with code {result.ExitCode}: {errSummary}",
                result.ExitCode);
        }

        // Post-install verification
        if (!Directory.Exists(config.Paths.MiddlewareHome))
            return StepExecutionResult.Fail(
                $"Middleware Home not created at '{config.Paths.MiddlewareHome}'. Check OUI logs.");

        _log.Info($"WebLogic installed successfully at: {config.Paths.MiddlewareHome}", "Install.WLS");
        return StepExecutionResult.Ok(
            $"WebLogic {config.WebLogicVersion} installed at {config.Paths.MiddlewareHome}", sw.Elapsed);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? FindJavaExe(DeploymentConfiguration config)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.Java.JavaHome))
            candidates.Add(Path.Combine(config.Java.JavaHome, "bin", "java.exe"));

        var javaHomeEnv = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHomeEnv))
            candidates.Add(Path.Combine(javaHomeEnv, "bin", "java.exe"));

        // Program Files fallbacks
        candidates.AddRange(new[]
        {
            @"C:\Program Files\Java\jdk1.8.0_202\bin\java.exe",
            @"C:\Program Files\Java\jdk-21\bin\java.exe",
            @"C:\Program Files\Java\jdk1.7.0_75\bin\java.exe",
        });

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveInstallerJar(DeploymentConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.InfrastructureInstallerPath)
            && File.Exists(config.InfrastructureInstallerPath))
            return config.InfrastructureInstallerPath;

        if (!string.IsNullOrWhiteSpace(config.WebLogicInstallerPath)
            && File.Exists(config.WebLogicInstallerPath))
            return config.WebLogicInstallerPath;

        if (!config.PayloadAcquisition.UseLocalRepositoryOnly)
        {
            var versionFolder = config.WebLogicVersion switch
            {
                Domain.Enums.WebLogicVersion.WLS_11g => "11g",
                Domain.Enums.WebLogicVersion.WLS_12c => "12c",
                Domain.Enums.WebLogicVersion.WLS_14c => "14c",
                Domain.Enums.WebLogicVersion.WLS_15c => "15c",
                _ => "12c"
            };

            var infraDir = Path.Combine(config.PayloadBasePath, versionFolder, "infrastructure");
            var wlsDir   = Path.Combine(config.PayloadBasePath, versionFolder, "weblogic");
            return WEDM.Engine.Payload.LocalPayloadPatternMatcher.FindBestMatch(infraDir, ["*infrastructure*.jar", "*.jar"])
                ?? WEDM.Engine.Payload.LocalPayloadPatternMatcher.FindBestMatch(wlsDir, ["*wls*.jar", "*.jar"]);
        }

        return null;
    }

    private static string GetInventoryPtr(DeploymentConfiguration config)
    {
        var ptr = Path.Combine(config.Paths.TempDirectory, "oraInst.loc");
        var content = $"inventory_loc={config.Paths.OracleInventory}\ninst_group=Administrators\n";
        Directory.CreateDirectory(config.Paths.TempDirectory);
        File.WriteAllText(ptr, content);
        return ptr;
    }

    /// <summary>Build argv tokens for java.exe (OUI silent install).</summary>
    private static List<string> BuildJavaArgumentList(
        DeploymentConfiguration config,
        string jarPath,
        string rspPath,
        string? silentXmlPath,
        string? invPtrPath)
    {
        var heap = $"-Xmx{config.Java.HeapSizeMb}m";

        if (config.WebLogicVersion == Domain.Enums.WebLogicVersion.WLS_11g)
        {
            if (string.IsNullOrWhiteSpace(silentXmlPath))
                throw new InvalidOperationException("WLS 11g requires a silent_xml path.");
            return new List<string> { heap, "-jar", jarPath, "-mode=silent", $"-silent_xml={silentXmlPath}" };
        }

        if (string.IsNullOrWhiteSpace(invPtrPath))
            throw new InvalidOperationException("Fusion Middleware install requires oraInst.loc path.");

        return new List<string>
        {
            heap,
            "-jar",
            jarPath,
            "-silent",
            "-responseFile",
            rspPath,
            "-invPtrLoc",
            invPtrPath
        };
    }

    /// <summary>Escape content placed inside a PowerShell single-quoted string.</summary>
    private static string EscapeForPsSingleQuotedString(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
