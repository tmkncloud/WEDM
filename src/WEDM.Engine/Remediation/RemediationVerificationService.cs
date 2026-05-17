using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Remediation;

public sealed class RemediationVerificationService : IRemediationVerificationService
{
    private readonly IOracleInventoryService _inventory;
    private readonly IOracleProcessManager   _processes;

    public RemediationVerificationService(
        IOracleInventoryService inventory,
        IOracleProcessManager processes)
    {
        _inventory = inventory;
        _processes = processes;
    }

    public RemediationVerificationResult Verify(DeploymentConfiguration config)
    {
        var mw  = config.Paths.MiddlewareHome;
        var inv = config.Paths.OracleInventory;
        var validation = _inventory.ValidateForInstall(mw, inv);
        var homeState  = _inventory.DetectHomeState(mw, inv);

        var findings = new List<string>(validation.Findings);

        var mwCleared = !Directory.Exists(mw) || !HasResidualContent(mw);
        if (mwCleared)
            findings.Add("Verification: middleware home directory removed or empty ✔");
        else
            findings.Add($"Verification: middleware home still has content at '{mw}'.");

        var locks = _inventory.DetectLocks(inv);
        var activeLocks = locks.Where(l => !l.IsStale).ToList();
        var noLocks = activeLocks.Count == 0;
        if (noLocks)
            findings.Add("Verification: no active Oracle inventory locks ✔");
        else
            findings.Add($"Verification: {activeLocks.Count} active inventory lock(s) remain.");

        var activeProcs = _processes.DetectMiddlewareProcesses()
            .Where(p => !string.IsNullOrWhiteSpace(p.CommandLine)
                        && p.CommandLine.Contains(mw, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var noProcs = activeProcs.Count == 0;
        if (noProcs)
            findings.Add("Verification: no active processes reference middleware home ✔");
        else
            findings.Add($"Verification: {activeProcs.Count} process(es) still reference middleware home.");

        var inventoryClean = validation.CanProceed
                               && homeState is OracleHomeState.Clean or OracleHomeState.Unknown;

        var passed = inventoryClean && mwCleared && noLocks && noProcs;

        return new RemediationVerificationResult
        {
            Passed                 = passed,
            HomeStateAfter         = homeState,
            RemediationStateAfter  = passed ? OracleRemediationState.Healthy : OracleRemediationState.PartialInstall,
            CanProceedWithInstall  = passed,
            MiddlewareDirectoryCleared = mwCleared,
            InventoryClean         = inventoryClean,
            NoActiveProcesses      = noProcs,
            NoActiveLocks          = noLocks,
            Findings               = findings,
        };
    }

    private static bool HasResidualContent(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch
        {
            return true;
        }
    }
}
