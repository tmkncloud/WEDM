using System.Text.Json;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Migration;

public sealed class JsonMigrationSessionStore : IMigrationSessionStore
{
    public async Task SaveAsync(MigrationConfiguration configuration, string filePath, CancellationToken cancellationToken = default)
    {
        configuration.LastSavedUtc = DateTimeOffset.UtcNow;
        var document = new MigrationSessionDocument
        {
            SchemaVersion = MigrationSessionDocument.CurrentSchemaVersion,
            SavedAtUtc    = DateTimeOffset.UtcNow,
            Configuration = configuration,
        };

        var json = JsonSerializer.Serialize(document, MigrationJsonOptions.Create());
        var dir  = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    public async Task<MigrationConfiguration> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(filePath, cancellationToken);
        var document = JsonSerializer.Deserialize<MigrationSessionDocument>(json, MigrationJsonOptions.Create())
            ?? throw new InvalidDataException("Migration session file is empty or invalid.");

        if (document.SchemaVersion > MigrationSessionDocument.CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Migration session schema v{document.SchemaVersion} is newer than this WEDM build supports.");

        return document.Configuration;
    }
}
