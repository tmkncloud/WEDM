using WEDM.Domain.Interfaces;
using WEDM.Engine.Workflow.Steps;

namespace Orchestration.Integration.Tests;

/// <summary>Mirrors <c>App.xaml.cs</c> rollback registration without WPF bootstrap.</summary>
internal static class TestRollbackFactories
{
    public static StepExecutorFactory CreateProductionLikeRollbackFactory(ILoggingService log)
    {
        var removeSvc = new RemoveWindowsServiceStep(log);

        var rollback = new Dictionary<string, IStepExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["RollbackOpatchApply"]       = new RollbackOpatchApplyStep(log),
            ["Remove-OracleFolders"]      = new RemoveOracleFoldersStep(log),
            ["Remove-JDK"]                = new RemoveJdkStep(log),
            ["Remove-JavaEnvVars"]        = new RemoveJavaEnvVarsStep(log),
            ["Remove-MiddlewareHome"]      = new RemoveMiddlewareHomeStep(log),
            ["Remove-Domain"]             = new RemoveDomainStep(log),
            ["Remove-OracleRegistryKeys"] = new RemoveOracleRegistryKeysStep(log),
            ["Remove-VCRedist"]          = new RemoveVcRedistRollbackStep(log),
            ["Remove-FormsReports"]       = new RemoveFormsReportsRollbackStep(log),
            ["Remove-OHS"]               = new RemoveOhsWebTierRollbackStep(log),
            ["Drop-RCUSchemas"]          = new DropRcuSchemasRollbackStep(log),
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
