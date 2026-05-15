using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Discovers installed middleware homes, domains, and inventory on the source host.</summary>
public interface IMiddlewareDiscoveryService
{
    Task<MiddlewareTopologySnapshot> DiscoverTopologyAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken = default);
}

/// <summary>Scans Forms / Reports module metadata for migration planning (future: deep scanner).</summary>
public interface IFormsEnvironmentScanner
{
    Task<FormsReportsMetadataSnapshot> ScanFormsEnvironmentAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken = default);
}

/// <summary>Analyzes WebLogic domain topology, clusters, and Node Manager posture.</summary>
public interface IWebLogicTopologyAnalyzer
{
    Task<MiddlewareTopologySnapshot> AnalyzeDomainAsync(
        MigrationEnvironmentProfile source,
        CancellationToken cancellationToken = default);
}

/// <summary>Migration transformation plan preview for summary dashboards.</summary>
public interface IConfigurationTransformationEngine
{
    Task<string> BuildTransformationPlanPreviewAsync(
        MigrationConfiguration configuration,
        CancellationToken cancellationToken = default);
}

/// <summary>Assesses source→target compatibility and produces enterprise risk findings.</summary>
public interface ICompatibilityAssessmentEngine
{
    Task<MigrationReadinessSnapshot> AssessAsync(
        MigrationConfiguration configuration,
        CancellationToken cancellationToken = default);

    IReadOnlyList<CompatibilityFinding> GetLastFindings();
}
