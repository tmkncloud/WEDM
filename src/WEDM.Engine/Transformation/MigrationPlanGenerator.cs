using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Migration;
using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation;

public sealed class MigrationPlanGenerator : IMigrationPlanGenerator
{
    public MigrationPlanDocument Generate(MigrationConfiguration configuration, TransformationExecutionResult result)
    {
        var path = MigrationVersionMatrix.DescribeUpgradePath(configuration.Source.Release, configuration.Target.Release);
        var plan = new MigrationPlanDocument
        {
            Title                  = configuration.Name,
            UpgradePath            = path,
            Strategy               = configuration.Strategy.ToString(),
            EstimatedEffortCategory = configuration.Readiness.EffortCategory.ToString(),
            OperatorSummary        = BuildOperatorSummary(configuration, result),
        };

        plan.Prerequisites.AddRange([
            "Complete discovery and compatibility assessment",
            "Provision target Fusion Middleware stack",
            "Validate backup and rollback procedures for source domain",
            "Review generated WLST scripts in migration workspace",
        ]);

        if (configuration.Readiness.BlockerCount > 0)
            plan.Prerequisites.Insert(0, $"Resolve {configuration.Readiness.BlockerCount} blocking compatibility item(s)");

        plan.Stages.AddRange([
            Stage(1, "Environment provisioning", "Install target WebLogic / Forms / Reports homes and JDK", 16),
            Stage(2, "Domain recreation", "Execute offline WLST domain creation scripts", 8),
            Stage(3, "Topology recreation", "Machines, clusters, managed servers, JDBC", 12),
            Stage(4, "Configuration transformation", "Apply generated config modernizations", 8),
            Stage(5, "Forms / Reports remediation", "Address modernization blockers and WebUtil", 24),
            Stage(6, "Validation gates", "Smoke test, performance baseline, security scan", 16),
            Stage(7, "Cutover preparation", "Parallel run or phased module migration per strategy", 8),
        ]);

        plan.RemediationTasks.AddRange(result.Remediations.Select(r => $"[{r.Severity}] {r.Title}"));
        plan.RollbackSteps.AddRange([
            "Preserve source domain and middleware homes unchanged",
            "Document DNS / load balancer switchback procedure",
            "Retain discovery snapshot and workspace manifest for audit",
            "Validate source environment health before re-enabling traffic",
        ]);

        plan.CutoverSteps.AddRange([
            "Freeze source application deployments",
            "Execute target domain startup and Node Manager enrollment",
            "Redirect traffic per migration strategy",
            "Execute post-migration validation checklist",
        ]);

        plan.PostMigrationValidation.AddRange([
            "Admin console and Node Manager connectivity",
            "Managed server health and cluster load balancing",
            "Forms / Reports functional smoke tests",
            "JVM and GC logging review on target JDK",
        ]);

        return plan;
    }

    private static string BuildOperatorSummary(MigrationConfiguration config, TransformationExecutionResult result)
    {
        return $"Migration plan for {config.Name}: {result.Artifacts.Count} artifacts generated with " +
               $"{result.Validation.Confidence} confidence. " +
               $"{result.Remediations.Count} remediation tasks documented. " +
               $"Workspace: {result.WorkspacePath}";
    }

    private static MigrationPlanStage Stage(int order, string name, string desc, double hours) => new()
    {
        Order           = order,
        Name            = name,
        Description     = desc,
        EstimatedHours  = hours,
    };
}
