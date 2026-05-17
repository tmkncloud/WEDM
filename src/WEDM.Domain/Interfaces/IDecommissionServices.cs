using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

public interface IEnvironmentDiscoveryService
{
    Task<EnvironmentTopology> DiscoverAsync(
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Decommission-specific Oracle inventory analysis and home detachment operations.
/// Distinct from <see cref="IOracleInventoryService"/> which handles the full install lifecycle.
/// </summary>
public interface IOracleInventoryAnalyzer
{
    OracleInventoryAnalysis Analyze(string? inventoryRoot, string? middlewareHome = null);

    Task<InventoryDetachResult> DetachHomeAsync(
        string oracleHomePath,
        string inventoryRoot,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public interface IOracleHomeValidator
{
    OracleHomeValidationResult ValidateForInstall(DeploymentConfiguration config);

    OracleHomeValidationResult ValidateForRemoval(DecommissionConfiguration config, EnvironmentTopology? topology = null);
}

public interface IOracleCleanupService
{
    Task<OracleCleanupResult> CleanupAsync(
        DecommissionConfiguration config,
        EnvironmentTopology topology,
        OracleCleanupMode mode,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public interface IOracleProcessManager
{
    IReadOnlyList<OracleProcessDescriptor> DetectMiddlewareProcesses();

    Task<ProcessStopResult> StopProcessesAsync(
        IEnumerable<OracleProcessDescriptor> processes,
        bool forceAfterTimeout,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default);
}

public interface IDeployOracleConflictDetector
{
    OracleConflictReport DetectConflicts(DeploymentConfiguration config);
}

public interface IInstallRetryIsolationService
{
    /// <summary>
    /// Legacy entry point used by the workflow engine retry loop.
    /// Mutates <paramref name="config"/> paths and returns basic isolation metadata.
    /// </summary>
    RetryIsolationContext PrepareRetryAttempt(
        DeploymentConfiguration config,
        string stepName,
        int attemptNumber);

    /// <summary>
    /// Builds a rich per-attempt <see cref="InstallerExecutionContext"/> with isolated
    /// temp, extraction, response file, and OUI log directories.
    /// Also sets <see cref="DeploymentConfiguration.CurrentInstallerContext"/> on
    /// <paramref name="config"/> so that OUI steps can consume it.
    /// </summary>
    InstallerExecutionContext BuildInstallerContext(
        DeploymentConfiguration config,
        string stepName,
        int attemptNumber,
        InstallerFailureClass previousFailureClass = InstallerFailureClass.Unknown);

    /// <summary>
    /// Runs pre-flight validation before an OUI attempt and logs results.
    /// Returns the validation result without blocking (callers decide on action).
    /// </summary>
    InstallerRetryPreflightResult RunPreflight(
        DeploymentConfiguration config,
        string stepName,
        int attemptNumber);
}

public interface IDecommissionWorkflowEngine
{
    IReadOnlyList<DeploymentStep> BuildStepPlan(DecommissionConfiguration config);

    Task<DecommissionReport> RunAsync(
        DecommissionConfiguration config,
        IReadOnlyList<DeploymentStep> steps,
        CancellationToken cancellationToken = default);
}
