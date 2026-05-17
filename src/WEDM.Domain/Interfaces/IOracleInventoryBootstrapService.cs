using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

public interface IOracleInventoryPathResolver
{
    InventoryPointerContext Resolve(DeploymentConfiguration config, InventoryPointerScope scope = InventoryPointerScope.DefaultCentral);
}

public interface IOracleInventorySkeletonFactory
{
    string BuildInventoryXml(DeploymentConfiguration config, BootstrapVersionStrategy strategy);
    string GetVersionProfile(DeploymentConfiguration config, BootstrapVersionStrategy strategy);
}

public interface IOracleInventoryBootstrapValidator
{
    InventoryBootstrapValidationResult Validate(string inventoryRoot, string inventoryXmlPath);
}

public interface IOracleInventoryBootstrapReportBuilder
{
    OracleInventoryBootstrapReport Build(
        InventoryBootstrapAssessment assessment,
        InventoryBootstrapPlan plan,
        IReadOnlyList<string> createdDirs,
        IReadOnlyList<string> writtenFiles,
        InventoryPointerContext? pointer,
        InventoryBootstrapValidationResult? validation,
        InventoryBootstrapExecutionOptions options,
        bool success);
}

public interface IOracleInventoryBootstrapService
{
    InventoryBootstrapAssessment Assess(DeploymentConfiguration config);

    Task<OracleInventoryBootstrapResult> EnsureInventoryReadyAsync(
        DeploymentConfiguration config,
        InventoryBootstrapExecutionOptions options,
        CancellationToken cancellationToken = default);

    bool ShouldAutoBootstrap(DeploymentConfiguration config, InventoryBootstrapAssessment assessment);
}
