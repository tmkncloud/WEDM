using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.OracleInventoryBootstrap;

public sealed class OracleInventoryBootstrapReportBuilder : IOracleInventoryBootstrapReportBuilder
{
    public OracleInventoryBootstrapReport Build(
        InventoryBootstrapAssessment assessment,
        InventoryBootstrapPlan plan,
        IReadOnlyList<string> createdDirs,
        IReadOnlyList<string> writtenFiles,
        InventoryPointerContext? pointer,
        InventoryBootstrapValidationResult? validation,
        InventoryBootstrapExecutionOptions options,
        bool success)
    {
        return new OracleInventoryBootstrapReport
        {
            DryRun             = options.DryRun,
            Success            = success,
            Strategy           = plan.Strategy,
            VersionProfile     = plan.VersionProfile,
            InventoryRoot      = plan.InventoryRoot,
            CreatedDirectories = createdDirs,
            WrittenFiles       = writtenFiles,
            PointerContext     = pointer,
            Validation         = validation,
            Warnings           = assessment.Safety.BlockingReasons.Count == 0
                ? []
                : assessment.Safety.BlockingReasons.ToList(),
            Errors             = success ? [] : ["Bootstrap did not complete successfully."],
            CompletedAt        = DateTimeOffset.UtcNow,
        };
    }
}
