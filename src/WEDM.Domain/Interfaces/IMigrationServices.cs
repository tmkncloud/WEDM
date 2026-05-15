using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Generates enterprise migration assessment reports (JSON and HTML).</summary>
public interface IMigrationReportWriter
{
    Task<string> WriteJsonAsync(MigrationConfiguration configuration, string outputDirectory, CancellationToken cancellationToken = default);

    Task<string> WriteHtmlAsync(MigrationConfiguration configuration, string outputDirectory, CancellationToken cancellationToken = default);
}

/// <summary>Persists and restores migration session plans using versioned DTOs.</summary>
public interface IMigrationSessionStore
{
    Task SaveAsync(MigrationConfiguration configuration, string filePath, CancellationToken cancellationToken = default);

    Task<MigrationConfiguration> LoadAsync(string filePath, CancellationToken cancellationToken = default);
}
