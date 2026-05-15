using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>
/// Rollbacks for steps where fully automated reversal is unsafe or unsupported.
/// These executors return <see cref="StepExecutionResult.OkWithManualFollowUp"/> so operators
/// receive explicit remediation guidance without WEDM falsely claiming the system was reverted.
/// </summary>
public sealed class RemoveVcRedistRollbackStep(ILoggingService log) : IStepExecutor
{
    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        log.Warning(
            "Remove-VCRedist: Automated VC++ Redistributable uninstall is not performed — " +
            "use Windows Apps & Features / msiexec against the product GUID documented by Microsoft.",
            "Rollback");
        return Task.FromResult(StepExecutionResult.OkWithManualFollowUp(
            "Manual: uninstall 'Microsoft Visual C++ 20xx Redistributable (x64)' from the server if required."));
    }
}

/// <summary>OHS / WebTier rollback — best-effort removal under middleware home when paths exist.</summary>
public sealed class RemoveOhsWebTierRollbackStep(ILoggingService log) : IStepExecutor
{
    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var mw = config.Paths.MiddlewareHome;
            if (string.IsNullOrWhiteSpace(mw) || !Directory.Exists(mw))
                return Task.FromResult(StepExecutionResult.OkWithManualFollowUp(
                    "Manual: OHS home unknown — remove Oracle HTTP Server / WebTier via Oracle inventory if installed."));

            foreach (var marker in new[] { "ohs", "Oracle_WT", "oracle_common" })
            {
                var candidate = Path.Combine(mw, marker);
                if (!Directory.Exists(candidate)) continue;
                TryDeleteTree(log, candidate);
            }

            log.Info("Remove-OHS: Best-effort cleanup attempted under middleware home.", "Rollback");
            return Task.FromResult(StepExecutionResult.OkWithManualFollowUp(
                "Manual: verify OHS binaries and Windows services are gone; use Oracle Universal Deinstaller if needed."));
        }
        catch (Exception ex)
        {
            log.Error("Remove-OHS rollback encountered an error.", ex, "Rollback");
            return Task.FromResult(StepExecutionResult.Fail($"Remove-OHS rollback failed: {ex.Message}", 1, ex));
        }
    }

    private static void TryDeleteTree(ILoggingService log, string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            log.Info($"Remove-OHS: Deleted '{path}'.", "Rollback");
        }
        catch (Exception ex)
        {
            log.Warning($"Remove-OHS: Could not delete '{path}': {ex.Message}", "Rollback");
        }
    }
}

/// <summary>Forms &amp; Reports rollback — best-effort directory cleanup.</summary>
public sealed class RemoveFormsReportsRollbackStep(ILoggingService log) : IStepExecutor
{
    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var mw = config.Paths.MiddlewareHome;
            if (string.IsNullOrWhiteSpace(mw) || !Directory.Exists(mw))
                return Task.FromResult(StepExecutionResult.OkWithManualFollowUp(
                    "Manual: remove Forms & Reports via Oracle inventory or OUI on this host."));

            foreach (var marker in new[] { "forms", "reports", "FRHome", "Oracle_FR" })
            {
                var candidate = Path.Combine(mw, marker);
                if (!Directory.Exists(candidate)) continue;
                try
                {
                    Directory.Delete(candidate, recursive: true);
                    log.Info($"Remove-FormsReports: Deleted '{candidate}'.", "Rollback");
                }
                catch (Exception ex)
                {
                    log.Warning($"Remove-FormsReports: Could not delete '{candidate}': {ex.Message}", "Rollback");
                }
            }

            return Task.FromResult(StepExecutionResult.OkWithManualFollowUp(
                "Manual: confirm FR components via Oracle inventory; rerun OUI deinstall if directories remain."));
        }
        catch (Exception ex)
        {
            log.Error("Remove-FormsReports rollback failed.", ex, "Rollback");
            return Task.FromResult(StepExecutionResult.Fail($"Remove-FormsReports rollback failed: {ex.Message}", 1, ex));
        }
    }
}

/// <summary>
/// RCU schema teardown is never executed silently — operators must use RCU drop or DBA scripts.
/// </summary>
public sealed class DropRcuSchemasRollbackStep(ILoggingService log) : IStepExecutor
{
    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var db = config.Database;
        log.Warning(
            "Drop-RCUSchemas: Automated schema drop is DISABLED. Use Oracle RCU Drop Repository or " +
            "DBA-provided scripts for STB / MDS / IAU / OPSS / FORMS prefix after confirming the correct schema owner.",
            "Rollback");

        var msg =
            $"Manual: connect to {db.Host}:{db.Port} ({db.ServiceName}) and drop RCU-managed schemas for this prefix " +
            $"using RCU or audited SQL. Schema prefix: {db.SchemaPrefix}.";
        return Task.FromResult(StepExecutionResult.OkWithManualFollowUp(msg));
    }
}
