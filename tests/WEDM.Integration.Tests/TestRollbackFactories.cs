using Moq;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Workflow.Steps;

namespace Orchestration.Integration.Tests;

/// <summary>Mirrors <c>App.xaml.cs</c> rollback registration without WPF bootstrap.</summary>
internal static class TestRollbackFactories
{
    /// <summary>
    /// Creates a no-op <see cref="IOracleInventoryService"/> suitable for integration tests
    /// that focus on workflow orchestration rather than inventory XML behaviour.
    /// All validation methods report CanProceed=true; no home is considered registered.
    /// </summary>
    public static IOracleInventoryService CreateNoOpInventoryService()
    {
        var mock = new Mock<IOracleInventoryService>();

        // Pre/post-install validation — always allow
        mock.Setup(s => s.ValidateForInstall(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string mw, string inv) => new OracleInventoryValidationResult
            {
                CanProceed             = true,
                HomeState              = OracleHomeState.Clean,
                TargetMiddlewareHome   = mw,
                OracleInventoryPath    = inv,
            });

        mock.Setup(s => s.ValidateAfterInstall(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string mw, string inv) => new OracleInventoryValidationResult
            {
                CanProceed             = true,
                HomeState              = OracleHomeState.RegisteredAndPresent,
                TargetMiddlewareHome   = mw,
                OracleInventoryPath    = inv,
            });

        // Removal — report "not registered" so rollback steps skip XML mutation
        mock.Setup(s => s.RemoveHomeEntry(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string mw, string inv) =>
                OracleInventoryRemovalResult.NotFound(inv));

        // Detection helpers
        mock.Setup(s => s.IsHomeRegistered(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);
        mock.Setup(s => s.DetectHomeState(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(OracleHomeState.Clean);
        mock.Setup(s => s.IsPartialInstall(It.IsAny<string>()))
            .Returns(false);
        mock.Setup(s => s.FindOrphanedHomes(It.IsAny<string>()))
            .Returns([]);
        mock.Setup(s => s.DetectLocks(It.IsAny<string>()))
            .Returns([]);
        mock.Setup(s => s.ReadSnapshot(It.IsAny<string>()))
            .Returns((OracleInventorySnapshot?)null);
        mock.Setup(s => s.ResolveInventoryXmlPath(It.IsAny<string>()))
            .Returns((string?)null);
        mock.Setup(s => s.BackupInventoryXml(It.IsAny<string>()))
            .Returns((string?)null);

        return mock.Object;
    }

    /// <summary>
    /// Creates a no-op <see cref="IOracleProcessManager"/> for integration tests that do not
    /// require real process management (detects no processes; stop is a no-op).
    /// </summary>
    public static IOracleProcessManager CreateNoOpProcessManager()
    {
        var mock = new Mock<IOracleProcessManager>();
        mock.Setup(m => m.DetectMiddlewareProcesses())
            .Returns([]);
        mock.Setup(m => m.StopProcessesAsync(
                It.IsAny<IEnumerable<OracleProcessDescriptor>>(),
                It.IsAny<bool>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessStopResult { StoppedCount = 0, FailedCount = 0 });
        return mock.Object;
    }

    public static StepExecutorFactory CreateProductionLikeRollbackFactory(ILoggingService log)
    {
        var inventory      = CreateNoOpInventoryService();
        var processManager = CreateNoOpProcessManager();
        var removeSvc      = new RemoveWindowsServiceStep(log);

        var rollback = new Dictionary<string, IStepExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["RollbackOpatchApply"]       = new RollbackOpatchApplyStep(log),
            ["Remove-OracleFolders"]      = new RemoveOracleFoldersStep(log, inventory),
            ["Remove-JDK"]                = new RemoveJdkStep(log),
            // Oracle-aware rollback executors mirror App.xaml.cs production registration
            ["Remove-JavaEnvVars"]        = new OracleJavaHomeRollbackExecutor(log),
            ["Remove-MiddlewareHome"]     = new OracleInstallRollbackExecutor(log, inventory, processManager),
            ["Remove-FormsReports"]       = new OracleFormsReportsRollbackExecutor(log, inventory, processManager),
            ["Remove-OHS"]               = new OracleOhsWebTierRollbackExecutor(log, inventory, processManager),
            ["Remove-Domain"]             = new RemoveDomainStep(log),
            ["Remove-OracleRegistryKeys"] = new RemoveOracleRegistryKeysStep(log),
            ["Remove-VCRedist"]           = new RemoveVcRedistRollbackStep(log),
            ["Drop-RCUSchemas"]           = new DropRcuSchemasRollbackStep(log),
        };

        static IStepExecutor? FallbackRollback(string action, RemoveWindowsServiceStep svc)
        {
            if (action.StartsWith("Remove-", StringComparison.OrdinalIgnoreCase) &&
                action.EndsWith("Service", StringComparison.OrdinalIgnoreCase))
                return svc;
            return null;
        }

        var forward = new Dictionary<string, IStepExecutor>(StringComparer.OrdinalIgnoreCase);
        return new StepExecutorFactory(
            forward,
            rollback,
            fallbackForward: _ => null,
            fallbackRollback: a => FallbackRollback(a, removeSvc));
    }
}
