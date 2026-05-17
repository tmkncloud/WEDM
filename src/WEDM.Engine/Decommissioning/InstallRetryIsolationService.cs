using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Installer;
using WEDM.Engine.ResponseFiles;

namespace WEDM.Engine.Decommissioning;

/// <summary>
/// Isolates Oracle installer state between retry attempts.
///
/// Responsibilities:
///   • Assign each retry a unique temp, extraction, and log directory
///   • Regenerate response files and inventory pointer per attempt
///   • Run pre-flight validation before each OUI invocation
///   • Purge stale OraInstall* extraction caches left by previous failures
///   • Emit structured retry telemetry to the deployment log
///
/// Integration points:
///   • <see cref="DeploymentWorkflowEngine"/> calls <see cref="PrepareRetryAttempt"/> before
///     each retry (legacy path — mutates config and sets CurrentInstallerContext).
///   • <see cref="WEDM.Engine.Workflow.Steps.InstallWebLogicStep"/> calls
///     <see cref="BuildInstallerContext"/> directly on the first attempt to get a rich context,
///     and uses <see cref="RunPreflight"/> to validate before launching OUI.
/// </summary>
public sealed class InstallRetryIsolationService : IInstallRetryIsolationService
{
    private readonly ILoggingService         _log;
    private readonly ResponseFileGenerator   _rspGen;
    private readonly IOracleCleanupService   _cleanup;
    private readonly IOracleInventoryService _inventory;

    public InstallRetryIsolationService(
        ILoggingService log,
        ResponseFileGenerator rspGen,
        IOracleCleanupService cleanup,
        IOracleInventoryService inventory)
    {
        _log       = log;
        _rspGen    = rspGen;
        _cleanup   = cleanup;
        _inventory = inventory;
    }

    // ── IInstallRetryIsolationService.PrepareRetryAttempt (legacy entry point) ──

    /// <inheritdoc/>
    public RetryIsolationContext PrepareRetryAttempt(
        DeploymentConfiguration config,
        string stepName,
        int attemptNumber)
    {
        if (!config.OracleLifecycle.IsolateRetries || attemptNumber <= 1)
        {
            return new RetryIsolationContext
            {
                IsolatedTempDirectory = config.Paths.TempDirectory,
                Actions               = ["Retry isolation disabled or first attempt — using base temp directory."],
            };
        }

        // Determine the previous failure class from the current installer context
        var prevClass = config.CurrentInstallerContext?.PreviousFailureClass
                        ?? InstallerFailureClass.Unknown;

        // Build rich context (also sets config.CurrentInstallerContext)
        var ctx = BuildInstallerContext(config, stepName, attemptNumber, prevClass);

        // Mutate TempDirectory for backward compatibility with all steps that read it directly
        config.Paths.TempDirectory = ctx.TempDirectory;

        LogRetryTelemetry(stepName, attemptNumber, prevClass, ctx);

        return new RetryIsolationContext
        {
            IsolatedTempDirectory   = ctx.TempDirectory,
            RegeneratedResponseFile = ctx.ResponseFilePath,
            Actions                 = ctx.CleanupPaths
                .Select(p => $"Cleanup path registered: {p}")
                .Concat([$"Isolated temp: {ctx.TempDirectory}",
                          $"Extraction dir: {ctx.ExtractionDirectory}",
                          $"Response file: {ctx.ResponseFilePath}",
                          $"Previous failure: {prevClass}"])
                .ToList(),
        };
    }

    // ── IInstallRetryIsolationService.BuildInstallerContext ─────────────────────

    /// <inheritdoc/>
    public InstallerExecutionContext BuildInstallerContext(
        DeploymentConfiguration config,
        string stepName,
        int attemptNumber,
        InstallerFailureClass previousFailureClass = InstallerFailureClass.Unknown)
    {
        var uniqueId = Guid.NewGuid();
        var tag      = $"{Sanitize(stepName)}-a{attemptNumber}-{uniqueId:N}";

        // Root temp base — on attempt 1, use base temp; on retries use attempt-specific subdir
        var tempBase = attemptNumber > 1
            ? Path.Combine(GetParentTemp(config), $"wedm-retry-{tag}")
            : config.Paths.TempDirectory;

        Directory.CreateDirectory(tempBase);

        // Extraction directory — always unique per attempt to prevent JAR cache pollution
        var extractionDir = Path.Combine(tempBase, "extract");
        Directory.CreateDirectory(extractionDir);

        // OUI log directory (we'll look here first in the log scanner)
        var logDir = Path.Combine(tempBase, "logs");
        Directory.CreateDirectory(logDir);

        // Regenerate response file into the isolated temp
        // (temporarily redirect config.Paths.TempDirectory so ResponseFileGenerator writes there)
        var savedTemp = config.Paths.TempDirectory;
        config.Paths.TempDirectory = tempBase;

        string rspPath;
        string? silentXmlPath = null;

        if (IsWebLogicStep(stepName))
        {
            rspPath = _rspGen.GenerateWebLogicResponseFile(config);
            if (config.WebLogicVersion == Domain.Enums.WebLogicVersion.WLS_11g)
                silentXmlPath = _rspGen.GenerateWls11gSilentXml(config);
        }
        else if (stepName.Contains("Forms", StringComparison.OrdinalIgnoreCase) ||
                 stepName.Contains("Reports", StringComparison.OrdinalIgnoreCase))
        {
            rspPath = _rspGen.GenerateFormsResponseFile(config);
        }
        else if (stepName.Contains("OHS", StringComparison.OrdinalIgnoreCase) ||
                 stepName.Contains("WebTier", StringComparison.OrdinalIgnoreCase))
        {
            rspPath = _rspGen.GenerateOhsResponseFile(config);
        }
        else
        {
            // Default to WebLogic for unrecognised step names
            rspPath = _rspGen.GenerateWebLogicResponseFile(config);
        }

        config.Paths.TempDirectory = savedTemp;

        // Write oraInst.loc into isolated temp
        var invPtrPath = WriteInventoryPointer(config, tempBase);

        // Collect cleanup paths (purge after attempt regardless of outcome)
        var cleanupPaths = new List<string> { extractionDir, logDir };
        if (attemptNumber > 1)
            cleanupPaths.Add(tempBase);  // whole attempt dir is disposable

        var ctx = new InstallerExecutionContext
        {
            AttemptNumber        = attemptNumber,
            UniqueId             = uniqueId,
            TempDirectory        = tempBase,
            ExtractionDirectory  = extractionDir,
            ResponseFilePath     = rspPath,
            SilentXmlPath        = silentXmlPath,
            InventoryPointerPath = invPtrPath,
            OuiLogDirectory      = logDir,
            PreviousFailureClass = previousFailureClass,
            CleanupPaths         = cleanupPaths.AsReadOnly(),
        };

        // Expose on config so OUI steps can consume it without a service reference
        config.CurrentInstallerContext = ctx;

        _log.Info(
            $"[InstallerContext] Built for '{stepName}' attempt {attemptNumber}: " +
            $"temp={tempBase} extract={extractionDir} rsp={rspPath}",
            "Installer.Context");

        return ctx;
    }

    // ── IInstallRetryIsolationService.RunPreflight ──────────────────────────────

    /// <inheritdoc/>
    public InstallerRetryPreflightResult RunPreflight(
        DeploymentConfiguration config,
        string stepName,
        int attemptNumber)
    {
        var preflight = new InstallerRetryPreflight(_inventory, _log);
        var result    = preflight.Validate(config, stepName, attemptNumber);

        _log.Info(
            $"[InstallerPreflight] '{stepName}' attempt {attemptNumber}: " +
            $"canProceed={result.CanProceed} " +
            $"findings={result.Findings.Count} " +
            $"blocking={result.BlockingItems.Count} " +
            $"actions={result.ActionsTaken.Count}",
            "Installer.Preflight");

        return result;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void LogRetryTelemetry(
        string stepName,
        int attemptNumber,
        InstallerFailureClass prevClass,
        InstallerExecutionContext ctx)
    {
        _log.Warning(
            $"[RetryTelemetry] *** Retry attempt {attemptNumber} for step '{stepName}' ***",
            "Installer.Retry");
        _log.Info(
            $"[RetryTelemetry] Previous failure classification: {prevClass}",
            "Installer.Retry");
        _log.Info(
            $"[RetryTelemetry] Remediation hint: {InstallerFailureClassifier.GetRemediationHint(prevClass)}",
            "Installer.Retry");
        _log.Info(
            $"[RetryTelemetry] Isolated temp directory: {ctx.TempDirectory}",
            "Installer.Retry");
        _log.Info(
            $"[RetryTelemetry] Extraction directory: {ctx.ExtractionDirectory}",
            "Installer.Retry");
        _log.Info(
            $"[RetryTelemetry] Regenerated response file: {ctx.ResponseFilePath}",
            "Installer.Retry");
        _log.Info(
            $"[RetryTelemetry] Inventory pointer: {ctx.InventoryPointerPath}",
            "Installer.Retry");
        _log.Info(
            $"[RetryTelemetry] OUI log directory: {ctx.OuiLogDirectory}",
            "Installer.Retry");
    }

    private static string WriteInventoryPointer(DeploymentConfiguration config, string targetDir)
    {
        var ptr     = Path.Combine(targetDir, "oraInst.loc");
        var content = $"inventory_loc={config.Paths.OracleInventory}\ninst_group=Administrators\n";
        File.WriteAllText(ptr, content);
        return ptr;
    }

    /// <summary>Parent of the current temp directory — used as the base for retry subdirectories.</summary>
    private static string GetParentTemp(DeploymentConfiguration config)
    {
        var parent = Path.GetDirectoryName(config.Paths.TempDirectory);
        return string.IsNullOrEmpty(parent) ? config.Paths.TempDirectory : parent;
    }

    private static bool IsWebLogicStep(string stepName)
        => stepName.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase)
        || stepName.Contains("WebLogic", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string value)
        => new string(value.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
}
