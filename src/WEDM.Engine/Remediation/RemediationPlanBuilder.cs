using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Remediation;

public sealed class RemediationPlanBuilder : IRemediationPlanBuilder
{
    public RemediationPlan Build(
        DeploymentConfiguration config,
        OracleRemediationAssessment assessment,
        SafetyAnalysisResult safety)
    {
        var lc      = config.OracleLifecycle;
        var actions = new List<RemediationAction>();
        var ctx     = PartialInstallClassifier.BuildContext(config);

        if (assessment.Classification == OracleRemediationState.Healthy)
        {
            return new RemediationPlan
            {
                Classification                    = assessment.Classification,
                Safety                            = safety,
                Actions                           = actions,
                CanAutoExecute                    = false,
                CanContinueDeploymentAfterSuccess = true,
                Summary                           = "No remediation required.",
            };
        }

        if (safety.IsSafeToRemediate || (!lc.SafeCleanupOnly && lc.AutoRemediationMode == AutoRemediationMode.Aggressive))
        {
            AddFilesystemActions(config, ctx, assessment, actions);
            AddInventoryActions(config, ctx, assessment, actions);
            AddLockActions(ctx, assessment, actions);
            AddIsolationArtifacts(config, ctx, actions);
        }

        var canAuto = safety.IsSafeToRemediate
                      && actions.Count > 0
                      && lc.EnableAutoRemediation
                      && lc.AutoRemediationMode is AutoRemediationMode.AutomaticSafeOnly or AutoRemediationMode.Aggressive;

        return new RemediationPlan
        {
            Classification                    = assessment.Classification,
            Safety                            = safety,
            Actions                           = actions,
            CanAutoExecute                    = canAuto,
            CanContinueDeploymentAfterSuccess = safety.IsSafeToRemediate,
            Summary                           = actions.Count == 0
                ? safety.Recommendation
                : $"{actions.Count} remediation action(s) planned for {assessment.Classification}.",
        };
    }

    private static void AddFilesystemActions(
        DeploymentConfiguration config,
        RemediationDiscoveryContext ctx,
        OracleRemediationAssessment assessment,
        List<RemediationAction> actions)
    {
        if (assessment.Classification is OracleRemediationState.PartialInstall
            or OracleRemediationState.FilesystemOnly
            or OracleRemediationState.SafeToClean)
        {
            if (Directory.Exists(ctx.MiddlewareHome))
            {
                actions.Add(new RemediationAction
                {
                    ActionType  = RemediationActionType.DeleteDirectory,
                    TargetPath  = ctx.MiddlewareHome,
                    Description = "Remove partial / orphan middleware home directory",
                    Risk        = RemediationRiskLevel.Medium,
                });
            }
        }
    }

    private static void AddInventoryActions(
        DeploymentConfiguration config,
        RemediationDiscoveryContext ctx,
        OracleRemediationAssessment assessment,
        List<RemediationAction> actions)
    {
        if (!config.OracleLifecycle.CleanupInventoryArtifacts)
            return;

        if (assessment.Classification == OracleRemediationState.StaleInventoryRegistration)
        {
            actions.Add(new RemediationAction
            {
                ActionType  = RemediationActionType.DetachInventoryHome,
                TargetPath  = ctx.MiddlewareHome,
                Description = "Detach stale inventory registration for middleware home",
                Risk        = RemediationRiskLevel.Low,
            });
        }
    }

    private static void AddLockActions(
        RemediationDiscoveryContext ctx,
        OracleRemediationAssessment assessment,
        List<RemediationAction> actions)
    {
        if (assessment.Classification != OracleRemediationState.Locked)
            return;

        var locksDir = Path.Combine(ctx.OracleInventoryPath, "locks");
        if (!Directory.Exists(locksDir))
            return;

        foreach (var file in Directory.EnumerateFiles(locksDir))
        {
            var info = new FileInfo(file);
            if ((DateTimeOffset.UtcNow - info.LastWriteTimeUtc) > TimeSpan.FromHours(4))
            {
                actions.Add(new RemediationAction
                {
                    ActionType  = RemediationActionType.RemoveStaleLockFile,
                    TargetPath  = file,
                    Description = "Remove stale Oracle inventory lock file",
                    Risk        = RemediationRiskLevel.Low,
                });
            }
        }
    }

    private static void AddIsolationArtifacts(
        DeploymentConfiguration config,
        RemediationDiscoveryContext ctx,
        List<RemediationAction> actions)
    {
        if (!config.OracleLifecycle.CleanupRetryIsolationArtifacts)
            return;

        if (!string.IsNullOrWhiteSpace(ctx.ExtractionDirectory) && Directory.Exists(ctx.ExtractionDirectory))
        {
            actions.Add(new RemediationAction
            {
                ActionType  = RemediationActionType.DeleteExtractionFolder,
                TargetPath  = ctx.ExtractionDirectory,
                Description = "Remove isolated OUI extraction directory from retry attempt",
                Risk        = RemediationRiskLevel.Low,
            });
        }

        if (!string.IsNullOrWhiteSpace(ctx.TempDirectory) && Directory.Exists(ctx.TempDirectory))
        {
            foreach (var oraDir in Directory.GetDirectories(ctx.TempDirectory, "OraInstall*", SearchOption.TopDirectoryOnly))
            {
                actions.Add(new RemediationAction
                {
                    ActionType  = RemediationActionType.DeleteRetryTempDirectory,
                    TargetPath  = oraDir,
                    Description = "Remove stale OraInstall extraction cache",
                    Risk        = RemediationRiskLevel.Low,
                });
            }

            foreach (var retryDir in Directory.GetDirectories(ctx.TempDirectory, "wedm-retry-*", SearchOption.TopDirectoryOnly))
            {
                actions.Add(new RemediationAction
                {
                    ActionType  = RemediationActionType.DeleteRetryTempDirectory,
                    TargetPath  = retryDir,
                    Description = "Remove WEDM retry isolation directory",
                    Risk        = RemediationRiskLevel.Low,
                });
            }
        }

        if (config.OracleLifecycle.CleanupGeneratedFiles
            && !string.IsNullOrWhiteSpace(ctx.ResponseFilePath)
            && File.Exists(ctx.ResponseFilePath))
        {
            actions.Add(new RemediationAction
            {
                ActionType  = RemediationActionType.DeleteGeneratedResponseFile,
                TargetPath  = ctx.ResponseFilePath,
                Description = "Remove stale generated OUI response file from failed attempt",
                Risk        = RemediationRiskLevel.Low,
            });
        }
    }
}
