using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Domain.Enums;
using WEDM.Engine.Diagnostics;
using WEDM.Engine.EnvironmentIsolation;
using WEDM.Engine.Installer;
using WEDM.Engine.Remediation;
using WEDM.Engine.ResponseFiles;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>
/// Executes Oracle WebLogic Server / Fusion Middleware Infrastructure silent installation.
///
/// Strategy:
///   1. Pre-install Oracle inventory validation (blocks on INST-07319 preconditions)
///   2. Run pre-flight checks (advisory) and log results
///   3. Generate OUI response file — or use the pre-generated isolated file from the retry context
///   4. Invoke java -jar &lt;installer.jar&gt; -silent -responseFile &lt;rsp&gt; as elevated process
///      (with -Djava.io.tmpdir scoped to the isolated extraction directory when in retry isolation mode)
///   5. Monitor oraInstall*.log for progress
///   6. Classify failure and record on context so the next retry attempt knows the previous failure class
///   7. Verify middleware home directories exist post-install
///   8. Post-install Oracle inventory validation (confirms registration)
///
/// Supports: WLS 11g (wls_generic.jar), WLS 12c/14c (infrastructure.jar / wls.jar)
/// </summary>
public sealed class InstallWebLogicStep : IStepExecutor
{
    private readonly IPowerShellExecutor           _ps;
    private readonly ILoggingService               _log;
    private readonly ResponseFileGenerator         _rspGen;
    private readonly IOracleInventoryService       _inventory;
    private readonly IInstallRetryIsolationService _retryIsolation;
    private readonly IEnvironmentIsolationService  _envIsolation;
    private readonly IOracleRemediationService       _remediation;
    private readonly IOracleInventoryBootstrapService? _inventoryBootstrap;

    public InstallWebLogicStep(
        IPowerShellExecutor ps,
        ILoggingService log,
        ResponseFileGenerator rspGen,
        IOracleInventoryService inventory,
        IInstallRetryIsolationService retryIsolation,
        IEnvironmentIsolationService envIsolation,
        IOracleRemediationService remediation,
        IOracleInventoryBootstrapService? inventoryBootstrap = null)
    {
        _ps             = ps;
        _log            = log;
        _rspGen         = rspGen;
        _inventory      = inventory;
        _retryIsolation = retryIsolation;
        _envIsolation   = envIsolation;
        _remediation    = remediation;
        _inventoryBootstrap = inventoryBootstrap;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── Phase 0: Retry isolation context ──────────────────────────────
        // config.CurrentInstallerContext is set by DeploymentWorkflowEngine (via PrepareRetryAttempt)
        // before each retry. On the first attempt it may be null — we build it now so that
        // ExtractionDirectory, ResponseFilePath, and InventoryPointerPath are always populated.
        var ctx          = config.CurrentInstallerContext;
        var attemptNumber = ctx?.AttemptNumber ?? 1;

        if (ctx is null)
        {
            ctx = _retryIsolation.BuildInstallerContext(config, step.Name, attemptNumber);
            _log.Info(
                $"[InstallWebLogic] First-attempt context built: " +
                $"temp={ctx.TempDirectory} extract={ctx.ExtractionDirectory}",
                "Install.WLS");
        }
        else
        {
            _log.Warning(
                $"[InstallWebLogic] Retry attempt {attemptNumber} — " +
                $"previousFailure={ctx.PreviousFailureClass} " +
                $"extract={ctx.ExtractionDirectory}",
                "Install.WLS");
        }

        // ── Phase 1: Pre-flight checks (advisory) ─────────────────────────
        var preflight = _retryIsolation.RunPreflight(config, step.Name, attemptNumber);
        if (!preflight.CanProceed)
        {
            _log.Warning(
                $"[InstallPreflight] {preflight.BlockingItems.Count} blocking issue(s) found before OUI launch " +
                $"(hard gate is inventory pre-install check below):",
                "Install.WLS");
            foreach (var b in preflight.BlockingItems)
                _log.Warning($"  BLOCKING: {b}", "Install.WLS");
        }
        foreach (var a in preflight.ActionsTaken)
            _log.Info($"  [Preflight] {a}", "Install.WLS");

        // ── Phase 1b: Bootstrap central inventory when missing ─────────────
        if (_inventoryBootstrap is not null)
        {
            var bootAssessment = _inventoryBootstrap.Assess(config);
            if (_inventoryBootstrap.ShouldAutoBootstrap(config, bootAssessment))
            {
                var pointerScope = attemptNumber > 1
                    ? InventoryPointerScope.RetryIsolation
                    : InventoryPointerScope.DefaultCentral;
                var bootResult = await _inventoryBootstrap.EnsureInventoryReadyAsync(
                    config,
                    new InventoryBootstrapExecutionOptions
                    {
                        Trigger       = "InstallInfrastructure",
                        PointerScope  = pointerScope,
                    },
                    cancellationToken);
                foreach (var dir in bootResult.Report.CreatedDirectories)
                    _log.Info($"  [InventoryBootstrap] Created: {dir}", "Install.WLS");
                if (!bootResult.Success)
                {
                    RecordFailureClass(config, InstallerFailureClass.InventoryConflict);
                    return StepExecutionResult.Fail(
                        $"Oracle central inventory bootstrap failed: {string.Join("; ", bootResult.Report.Errors)}",
                        exitCode: -10);
                }
            }
        }

        // ── Phase 2: Pre-install Oracle inventory validation (hard gate) ───
        var preCheck = _inventory.ValidateForInstall(
            config.Paths.MiddlewareHome,
            config.Paths.OracleInventory);

        LogInventoryValidation(preCheck, "Pre-install");

        if (!preCheck.CanProceed)
        {
            preCheck = await TryAutoRemediateAndRevalidateAsync(config, preCheck, cancellationToken)
                       ?? preCheck;
        }

        if (!preCheck.CanProceed)
        {
            var findings    = string.Join(" | ", preCheck.Findings);
            var remediation = string.Join(" | ", preCheck.RemediationSteps);
            _log.Error(
                $"Oracle inventory pre-install validation blocked OUI launch. State={preCheck.HomeState}. " +
                $"Findings: {findings}",
                category: "Install.WLS");
            RecordFailureClass(config, InstallerFailureClass.InventoryConflict);
            return StepExecutionResult.Fail(
                $"Oracle inventory pre-install check failed (state: {preCheck.HomeState}). " +
                $"{findings} Remediation: {remediation}",
                exitCode: -10);
        }

        // ── Phase 3: Locate JDK and installer JAR ─────────────────────────
        var javaExe = FindJavaExe(config);
        if (javaExe is null)
        {
            RecordFailureClass(config, InstallerFailureClass.JavaLaunchFailure);
            return StepExecutionResult.Fail("java.exe not found. JDK must be installed before WebLogic.");
        }

        var jarPath = ResolveInstallerJar(config);
        if (jarPath is null || !File.Exists(jarPath))
        {
            RecordFailureClass(config, InstallerFailureClass.Unknown);
            return StepExecutionResult.Fail($"WebLogic installer JAR not found. Searched: {jarPath}");
        }

        // ── Phase 4: Resolve response file, silent XML, inventory pointer ──
        // Use pre-generated isolated paths from context; fall back to fresh generation.
        var rspPath      = ctx.ResponseFilePath;
        var silentXmlPath = ctx.SilentXmlPath;
        var invPtrPath   = ctx.InventoryPointerPath;

        if (string.IsNullOrEmpty(rspPath) || !File.Exists(rspPath))
        {
            _log.Info("Generating WebLogic response file (context path missing or stale)...", "Install.WLS");
            rspPath = _rspGen.GenerateWebLogicResponseFile(config);
        }

        if (config.WebLogicVersion == Domain.Enums.WebLogicVersion.WLS_11g
            && (string.IsNullOrEmpty(silentXmlPath) || !File.Exists(silentXmlPath)))
            silentXmlPath = _rspGen.GenerateWls11gSilentXml(config);

        if (config.WebLogicVersion != Domain.Enums.WebLogicVersion.WLS_11g
            && (string.IsNullOrEmpty(invPtrPath) || !File.Exists(invPtrPath)))
            invPtrPath = GetInventoryPtr(config);

        _log.Info($"Response file: {rspPath}", "Install.WLS");
        _log.Info($"Extraction dir (java.io.tmpdir): {ctx.ExtractionDirectory}", "Install.WLS");

        var argList = BuildJavaArgumentList(
            config, jarPath, rspPath, silentXmlPath, invPtrPath, ctx.ExtractionDirectory);
        _log.Info(
            $"Launching: {javaExe} {string.Join(" ", argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}",
            "Install.WLS");

        // ── Phase 5: Elevated OUI invocation ──────────────────────────────
        // Build isolation preamble: clears stale Oracle vars, scopes TEMP/TMP,
        // sanitizes PATH, and sets JAVA_HOME for the OUI JVM subprocess.
        var ouiEnv = config.EnvironmentContext is { } envCtx
            ? _envIsolation.BuildIsolatedEnvironment(OracleTool.OUI, envCtx)
            : null;

        var isolationPreamble = ouiEnv?.PowerShellPreamble ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(isolationPreamble))
            _log.Info($"[InstallWebLogic] Injecting OUI isolation preamble ({ouiEnv!.ClearVariables.Count} clears, {ouiEnv.SetVariables.Count} sets).", "Install.WLS");
        else
            _log.Warning("[InstallWebLogic] No EnvironmentContext — OUI runs in ambient machine environment.", "Install.WLS");

        // Elevated PowerShell: pass string[] to -ArgumentList so OUI receives correct argv.
        var psArgList = string.Join(", ", argList.Select(a => $"'{EscapeForPsSingleQuotedString(a)}'"));
        var psCommand = $@"
$ErrorActionPreference = 'Continue'
{isolationPreamble}
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
            workingDirectory: ctx.TempDirectory,
            runAsAdministrator: true,
            cancellationToken: cancellationToken,
            operationTimeout: ouiTimeout);

        sw.Stop();

        // ── Phase 6: OUI log scan ──────────────────────────────────────────
        // Check the attempt-scoped log dir first (populated by OUI when -Djava.io.tmpdir is set),
        // then fall back to the central inventory log locations.
        var logPath = OracleInstallLogScanner.FindLatestOuiStyleLog(
            config.Paths.OracleInventory,
            config.Paths.MiddlewareHome,
            ctx.OuiLogDirectory,        // per-attempt scoped log dir (searched first)
            config.Paths.TempDirectory);
        if (logPath is not null)
        {
            _log.Info($"Latest OUI / cfgtoollogs candidate: {logPath}", "Install.WLS");
            _log.Verbose(OracleInstallLogScanner.ReadLogTail(logPath), "Install.WLS.LogTail");
        }
        else
            _log.Warning("No OUI install log located yet (check inventory and cfgtoollogs after failure).", "Install.WLS");

        // ── Phase 7: Failure classification ───────────────────────────────
        if (result.TimedOut)
        {
            RecordFailureClass(config, InstallerFailureClass.Timeout);
            return StepExecutionResult.Fail(
                $"WebLogic installer timed out after {ouiTimeout}. See log tail above.", -2);
        }

        if (result.ExitCode != 0)
        {
            var failureClass = InstallerFailureClassifier.Classify(result.ExitCode, result.Errors);
            RecordFailureClass(config, failureClass);

            _log.Warning(
                $"[InstallWebLogic] Failure class: {failureClass} — " +
                $"{InstallerFailureClassifier.GetRemediationHint(failureClass)}",
                "Install.WLS");

            var errSummary = result.Errors.Length > 500 ? result.Errors[..500] + "…" : result.Errors;
            return StepExecutionResult.Fail(
                $"WebLogic installer exited with code {result.ExitCode}: {errSummary}",
                result.ExitCode);
        }

        // ── Phase 8: Filesystem post-install check ─────────────────────────
        if (!Directory.Exists(config.Paths.MiddlewareHome))
            return StepExecutionResult.Fail(
                $"Middleware Home not created at '{config.Paths.MiddlewareHome}'. Check OUI logs.");

        // ── Phase 9: Oracle inventory post-install validation ──────────────
        var postCheck = _inventory.ValidateAfterInstall(
            config.Paths.MiddlewareHome,
            config.Paths.OracleInventory);

        LogInventoryValidation(postCheck, "Post-install");

        if (!postCheck.CanProceed)
        {
            // Filesystem structure is incomplete even though OUI exited 0 — treat as failure
            var findings = string.Join(" | ", postCheck.Findings);
            _log.Error($"Oracle inventory post-install validation failed: {findings}", category: "Install.WLS");
            return StepExecutionResult.Fail(
                $"OUI exited successfully but post-install validation failed: {findings}", exitCode: -11);
        }

        _log.Info($"WebLogic installed successfully at: {config.Paths.MiddlewareHome}", "Install.WLS");

        // ── Phase 10: Capture rollback compensation ────────────────────────
        // Populate step.RollbackCompensation so that OracleInstallRollbackExecutor can
        // precisely reverse the state created by this step without relying solely on config paths.
        step.RollbackCompensation = new OracleRollbackCompensation
        {
            OracleHomePaths        = [config.Paths.MiddlewareHome],
            OracleInventoryPath    = config.Paths.OracleInventory,
            InventorySnapshotBefore = preCheck.Snapshot,
            // Services are registered by separate steps (Register-*Service) — not this step
            CreatedServiceNames    = [],
            // Env vars are set by ConfigureJavaHome, not here
            SetEnvironmentVariableNames = [],
            // Track WEDM-generated files written by this step
            GeneratedFilePaths     = BuildGeneratedFilePaths(ctx, rspPath, silentXmlPath, invPtrPath),
            AppliedPatchIds        = [],
            CapturedAt             = DateTimeOffset.UtcNow
        };

        _log.Info(
            $"[InstallWebLogic] Rollback compensation captured: " +
            $"home={config.Paths.MiddlewareHome}, " +
            $"files={step.RollbackCompensation.GeneratedFilePaths.Count}",
            "Install.WLS");

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

    /// <summary>
    /// Build argv tokens for java.exe (OUI silent install).
    /// When <paramref name="extractionDir"/> is provided (retry isolation mode) it is passed as
    /// <c>-Djava.io.tmpdir</c> so OUI extracts its JAR into the per-attempt directory rather than
    /// the shared system temp, preventing cross-attempt JAR cache pollution.
    /// </summary>
    private static List<string> BuildJavaArgumentList(
        DeploymentConfiguration config,
        string jarPath,
        string rspPath,
        string? silentXmlPath,
        string? invPtrPath,
        string? extractionDir = null)
    {
        var heap = $"-Xmx{config.Java.HeapSizeMb}m";

        // -Djava.io.tmpdir scopes OUI's JAR extraction to the per-attempt isolated directory.
        var tmpDirArg = !string.IsNullOrEmpty(extractionDir)
            ? $"-Djava.io.tmpdir={extractionDir}"
            : null;

        if (config.WebLogicVersion == Domain.Enums.WebLogicVersion.WLS_11g)
        {
            if (string.IsNullOrWhiteSpace(silentXmlPath))
                throw new InvalidOperationException("WLS 11g requires a silent_xml path.");

            var args11g = new List<string> { heap };
            if (tmpDirArg is not null) args11g.Add(tmpDirArg);
            args11g.AddRange(["-jar", jarPath, "-mode=silent", $"-silent_xml={silentXmlPath}"]);
            return args11g;
        }

        if (string.IsNullOrWhiteSpace(invPtrPath))
            throw new InvalidOperationException("Fusion Middleware install requires oraInst.loc path.");

        var args = new List<string> { heap };
        if (tmpDirArg is not null) args.Add(tmpDirArg);
        args.AddRange(["-jar", jarPath, "-silent", "-responseFile", rspPath, "-invPtrLoc", invPtrPath]);
        return args;
    }

    /// <summary>Escape content placed inside a PowerShell single-quoted string.</summary>
    private static string EscapeForPsSingleQuotedString(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// Replaces <see cref="DeploymentConfiguration.CurrentInstallerContext"/> with a copy that has
    /// <see cref="InstallerExecutionContext.PreviousFailureClass"/> set to the classified failure so
    /// that the next <c>PrepareRetryAttempt</c> call can read it via
    /// <c>config.CurrentInstallerContext?.PreviousFailureClass</c>.
    /// </summary>
    private static void RecordFailureClass(DeploymentConfiguration config, InstallerFailureClass failureClass)
    {
        var ctx = config.CurrentInstallerContext;
        if (ctx is null) return;

        config.CurrentInstallerContext = new InstallerExecutionContext
        {
            AttemptNumber        = ctx.AttemptNumber,
            UniqueId             = ctx.UniqueId,
            TempDirectory        = ctx.TempDirectory,
            ExtractionDirectory  = ctx.ExtractionDirectory,
            ResponseFilePath     = ctx.ResponseFilePath,
            SilentXmlPath        = ctx.SilentXmlPath,
            InventoryPointerPath = ctx.InventoryPointerPath,
            OuiLogDirectory      = ctx.OuiLogDirectory,
            PreviousFailureClass = failureClass,
            CleanupPaths         = ctx.CleanupPaths,
        };
    }

    /// <summary>
    /// Collects the WEDM-generated files written by this install step for rollback cleanup.
    /// Includes the OUI response file, WLS 11g silent.xml, and oraInst.loc pointer file.
    /// </summary>
    private static List<string> BuildGeneratedFilePaths(
        InstallerExecutionContext ctx,
        string?                   rspPath,
        string?                   silentXmlPath,
        string?                   invPtrPath)
    {
        var paths = new List<string>();

        // Response file — always present
        if (!string.IsNullOrWhiteSpace(rspPath))        paths.Add(rspPath);
        // WLS 11g silent.xml
        if (!string.IsNullOrWhiteSpace(silentXmlPath))  paths.Add(silentXmlPath);
        // oraInst.loc inventory pointer
        if (!string.IsNullOrWhiteSpace(invPtrPath))     paths.Add(invPtrPath);
        // Any extra cleanup paths registered by the isolation service
        paths.AddRange(ctx.CleanupPaths.Where(p => !paths.Contains(p, StringComparer.OrdinalIgnoreCase)));

        return paths;
    }

    private async Task<OracleInventoryValidationResult?> TryAutoRemediateAndRevalidateAsync(
        DeploymentConfiguration config,
        OracleInventoryValidationResult failedCheck,
        CancellationToken cancellationToken)
    {
        var assessment = _remediation.Assess(config, "InstallInfrastructure");
        if (!_remediation.ShouldAutoRemediate(config, assessment))
        {
            _log.Warning(
                $"[Remediation] Partial install detected (state={failedCheck.HomeState}) but auto-remediation is " +
                $"disabled or unsafe (classification={assessment.Classification}, mode={config.OracleLifecycle.AutoRemediationMode}).",
                "Install.WLS");
            return null;
        }

        var attempts = config.RemediationReports.Count(r => !r.DryRun);
        if (attempts >= config.OracleLifecycle.MaxRemediationAttempts)
        {
            _log.Warning(
                $"[Remediation] Max remediation attempts ({config.OracleLifecycle.MaxRemediationAttempts}) reached.",
                "Install.WLS");
            return null;
        }

        _log.Info(
            $"[Remediation] Auto-remediating partial install before OUI (classification={assessment.Classification})...",
            "Install.WLS");

        var result = await _remediation.ExecuteAsync(
            config,
            new RemediationExecutionOptions
            {
                DryRun  = false,
                Trigger = "InstallInfrastructure",
            },
            cancellationToken);

        foreach (var action in result.Report.ExecutedActions)
            _log.Info($"  [Remediation] {action.ActionType}: {action.Outcome} — {action.TargetPath}", "Install.WLS");

        if (!result.Success || !config.OracleLifecycle.AutoContinueAfterRemediation)
            return null;

        var revalidated = _inventory.ValidateForInstall(
            config.Paths.MiddlewareHome,
            config.Paths.OracleInventory);

        LogInventoryValidation(revalidated, "Post-remediation");
        return revalidated.CanProceed ? revalidated : null;
    }

    private void LogInventoryValidation(OracleInventoryValidationResult result, string phase)
    {
        var level = result.CanProceed ? "INFO" : "WARN";
        _log.Info(
            $"[{phase}] Oracle inventory: state={result.HomeState} canProceed={result.CanProceed}",
            "Install.WLS");
        foreach (var f in result.Findings)
            _log.Info($"  [{phase}] {f}", "Install.WLS");
        foreach (var r in result.RemediationSteps)
            _log.Warning($"  [{phase}] REMEDIATION: {r}", "Install.WLS");
    }
}
