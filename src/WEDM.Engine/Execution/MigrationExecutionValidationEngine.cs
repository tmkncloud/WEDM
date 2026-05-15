using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Execution;

public sealed class MigrationExecutionValidationEngine : IMigrationExecutionValidationEngine
{
    public ExecutionValidationSummary ValidatePostExecution(MigrationConfiguration configuration, MigrationExecutionResult result)
    {
        var messages = new List<string>();
        var passed = true;

        if (result.WlstExecutions.Any(w => !w.DryRun && !w.Success))
        {
            passed = false;
            messages.Add("One or more WLST scripts failed — review execution logs before proceeding.");
        }

        var targetDomain = result.RollbackManifest.ModifiedTargets.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(targetDomain) && Directory.Exists(Path.Combine(targetDomain, "config")))
            messages.Add("Target domain config directory detected.");
        else if (!result.DryRun)
            messages.Add("Target domain config not found — verify offline domain creation script results.");

        if (result.Preflight.BlockerCount > 0)
        {
            passed = false;
            messages.Add("Preflight blockers were present at execution start.");
        }

        return new ExecutionValidationSummary
        {
            Passed     = passed && result.Outcome is MigrationExecutionOutcome.Completed or MigrationExecutionOutcome.CompletedWithWarnings,
            Confidence = passed ? TransformationConfidenceKind.High : TransformationConfidenceKind.Low,
            Messages   = messages,
        };
    }

    public ExecutionValidationSummary ValidateStage(
        MigrationConfiguration configuration,
        MigrationExecutionStageKind stage,
        MigrationExecutionResult result)
    {
        var messages = new List<string>();
        switch (stage)
        {
            case MigrationExecutionStageKind.WlstExecution:
                foreach (var w in result.WlstExecutions.Where(x => !x.DryRun))
                {
                    if (!w.Success)
                        messages.Add($"WLST failed: {w.ScriptName} (exit {w.ExitCode})");
                }
                break;
            case MigrationExecutionStageKind.DomainRecreationValidation:
                var domainHome = result.RollbackManifest.ModifiedTargets.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(domainHome) || !Directory.Exists(Path.Combine(domainHome, "config", "config.xml")))
                    messages.Add("Domain config.xml not found on target — domain recreation may not have completed.");
                break;
        }

        return new ExecutionValidationSummary
        {
            Passed     = messages.Count == 0,
            Confidence = messages.Count == 0 ? TransformationConfidenceKind.High : TransformationConfidenceKind.Moderate,
            Messages   = messages,
        };
    }
}
