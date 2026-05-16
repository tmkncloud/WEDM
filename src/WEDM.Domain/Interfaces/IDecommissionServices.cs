using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

public interface IEnvironmentDiscoveryService
{
    Task<EnvironmentTopology> DiscoverAsync(
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default);
}

public interface IOracleInventoryService
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
    RetryIsolationContext PrepareRetryAttempt(
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
