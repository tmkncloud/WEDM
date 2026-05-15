using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

/// <summary>
/// Versioned migration session envelope for export/import and resume workflows.
/// </summary>
public sealed class MigrationSessionDocument
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("savedAtUtc")]
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("product")]
    public string Product { get; set; } = "WEDM";

    [JsonPropertyName("configuration")]
    public MigrationConfiguration Configuration { get; set; } = new();
}
