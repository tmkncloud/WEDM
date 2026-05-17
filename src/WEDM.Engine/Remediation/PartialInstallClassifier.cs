using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Remediation;

public sealed class PartialInstallClassifier : IPartialInstallClassifier
{
    private readonly IOracleInventoryService       _inventory;
    private readonly IOracleInventoryAnalyzer      _inventoryAnalyzer;
    private readonly IOracleHomeSafetyAnalyzer     _safety;

    public PartialInstallClassifier(
        IOracleInventoryService inventory,
        IOracleInventoryAnalyzer inventoryAnalyzer,
        IOracleHomeSafetyAnalyzer safety)
    {
        _inventory         = inventory;
        _inventoryAnalyzer = inventoryAnalyzer;
        _safety            = safety;
    }

    public OracleRemediationAssessment Classify(DeploymentConfiguration config, RemediationDiscoveryContext? context = null)
    {
        context ??= BuildContext(config);

        var homeState   = _inventory.DetectHomeState(context.MiddlewareHome, context.OracleInventoryPath);
        var invAnalysis = _inventoryAnalyzer.Analyze(context.OracleInventoryPath, context.MiddlewareHome);
        var locks       = _inventory.DetectLocks(context.OracleInventoryPath);
        var activeLocks = locks.Where(l => !l.IsStale).ToList();

        var classification = MapHomeState(homeState, invAnalysis, activeLocks.Count > 0, context);
        var safety         = _safety.Analyze(context, classification);

        if (safety.IsSafeToRemediate && IsRemediable(classification))
            classification = OracleRemediationState.SafeToClean;
        else if (!safety.IsSafeToRemediate && IsRemediable(classification))
            classification = OracleRemediationState.UnsafeToClean;

        var issues = BuildIssues(classification, homeState, context);
        var requires = classification is OracleRemediationState.PartialInstall
            or OracleRemediationState.FilesystemOnly
            or OracleRemediationState.Orphaned
            or OracleRemediationState.StaleInventoryRegistration
            or OracleRemediationState.SafeToClean
            or OracleRemediationState.UnsafeToClean
            or OracleRemediationState.Locked;

        var assessment = new OracleRemediationAssessment
        {
            Classification       = classification,
            HomeState            = homeState,
            Safety               = safety,
            Issues               = issues,
            RequiresRemediation  = requires,
            CanAutoRemediate     = safety.IsSafeToRemediate && classification == OracleRemediationState.SafeToClean,
        };

        return assessment;
    }

    internal static RemediationDiscoveryContext BuildContext(DeploymentConfiguration config)
    {
        var ctx = config.CurrentInstallerContext;
        return new RemediationDiscoveryContext
        {
            MiddlewareHome              = config.Paths.MiddlewareHome,
            OracleInventoryPath         = config.Paths.OracleInventory,
            TempDirectory                 = config.Paths.TempDirectory,
            ExtractionDirectory           = ctx?.ExtractionDirectory,
            ResponseFilePath              = ctx?.ResponseFilePath,
            SnapshotDirectory             = config.Paths.SnapshotDirectory,
            ReportsDirectory              = config.Paths.ReportsDirectory,
            TriggerStep                   = "InstallInfrastructure",
            AttemptNumber                 = ctx?.AttemptNumber ?? 1,
            PreviousFailureClass          = ctx?.PreviousFailureClass,
            StaleInstallActivityMinutes   = config.OracleLifecycle.StaleInstallActivityMinutes,
        };
    }

    private static OracleRemediationState MapHomeState(
        OracleHomeState homeState,
        OracleInventoryAnalysis inv,
        bool activeLock,
        RemediationDiscoveryContext context)
    {
        if (activeLock)
            return OracleRemediationState.Locked;

        foreach (var home in inv.Homes.Where(h =>
            h.Path.Equals(context.MiddlewareHome, StringComparison.OrdinalIgnoreCase)))
        {
            if (home.IsStale)
                return OracleRemediationState.StaleInventoryRegistration;
            if (home.PathExists)
                return OracleRemediationState.PartialInstall;
        }

        return homeState switch
        {
            OracleHomeState.PartialInstall       => OracleRemediationState.PartialInstall,
            OracleHomeState.UnregisteredInstall  => OracleRemediationState.FilesystemOnly,
            OracleHomeState.RegisteredOrphaned   => OracleRemediationState.StaleInventoryRegistration,
            OracleHomeState.RegisteredAndPresent => OracleRemediationState.Healthy,
            OracleHomeState.InventoryLocked      => OracleRemediationState.Locked,
            OracleHomeState.Clean                => OracleRemediationState.Healthy,
            _                                    => OracleRemediationState.Unknown,
        };
    }

    private static bool IsRemediable(OracleRemediationState state) =>
        state is OracleRemediationState.PartialInstall
            or OracleRemediationState.FilesystemOnly
            or OracleRemediationState.Orphaned
            or OracleRemediationState.StaleInventoryRegistration
            or OracleRemediationState.Locked;

    private static List<RemediationIssue> BuildIssues(
        OracleRemediationState classification,
        OracleHomeState homeState,
        RemediationDiscoveryContext context)
    {
        var list = new List<RemediationIssue>();
        if (classification == OracleRemediationState.Healthy)
            return list;

        list.Add(new RemediationIssue
        {
            Code    = $"Remediation.{classification}",
            State   = classification,
            Message = classification switch
            {
                OracleRemediationState.PartialInstall =>
                    $"Middleware home '{context.MiddlewareHome}' contains partial Oracle install artifacts and is not registered in central inventory.",
                OracleRemediationState.FilesystemOnly =>
                    $"Directory '{context.MiddlewareHome}' exists on disk without a valid central inventory registration.",
                OracleRemediationState.StaleInventoryRegistration =>
                    "Inventory references a home path that no longer exists on disk.",
                OracleRemediationState.Locked =>
                    "Oracle central inventory has active lock files — installer may still be running.",
                OracleRemediationState.SafeToClean =>
                    "Partial install detected; automated cleanup is safe to execute.",
                OracleRemediationState.UnsafeToClean =>
                    "Partial install detected but active processes or services block automated cleanup.",
                _ => $"Oracle remediation state: {classification} (home={homeState})",
            },
            Risk = classification == OracleRemediationState.UnsafeToClean
                ? RemediationRiskLevel.High
                : RemediationRiskLevel.Medium,
        });

        return list;
    }
}
