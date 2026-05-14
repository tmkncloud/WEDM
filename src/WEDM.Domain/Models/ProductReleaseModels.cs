using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

/// <summary>Logical release train for future update feeds (no network transport in core).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WedmReleaseChannelKind
{
    Dev,
    Beta,
    Stable,
    LongTermSupport
}

/// <summary>Optional sidecar metadata shipped next to WEDM.exe (Resources/wedm-product.json).</summary>
public sealed class WedmProductSidecar
{
    [JsonPropertyName("productName")]
    public string? ProductName { get; set; }

    [JsonPropertyName("releaseChannel")]
    public string? ReleaseChannel { get; set; }

    [JsonPropertyName("releaseNotesRelativePath")]
    public string? ReleaseNotesRelativePath { get; set; }

    [JsonPropertyName("updateFeedRelativePath")]
    public string? UpdateFeedRelativePath { get; set; }
}

/// <summary>Portable update manifest (local file today; future HTTP feed consumer).</summary>
public sealed class WedmUpdateManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("channel")]
    public WedmReleaseChannelKind Channel { get; set; } = WedmReleaseChannelKind.Stable;

    [JsonPropertyName("availableVersion")]
    public string AvailableVersion { get; set; } = string.Empty;

    [JsonPropertyName("packageFeedUri")]
    public string? PackageFeedUri { get; set; }

    [JsonPropertyName("notesUri")]
    public string? NotesUri { get; set; }
}

/// <summary>Enterprise release bundle descriptor (checksums produced by packaging scripts).</summary>
public sealed class WedmReleaseBundleManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("productVersion")]
    public string ProductVersion { get; set; } = string.Empty;

    [JsonPropertyName("releaseChannel")]
    public string ReleaseChannel { get; set; } = string.Empty;

    [JsonPropertyName("publishedUtc")]
    public DateTimeOffset PublishedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("artifacts")]
    public List<WedmReleaseArtifact> Artifacts { get; set; } = new();
}

public sealed class WedmReleaseArtifact
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>Resolved product identity for UI and reports (no secrets).</summary>
public sealed record ProductVersionSnapshot(
    string ProductName,
    string DisplayVersion,
    string InformationalVersion,
    string UiAssemblyFileVersion,
    string EngineAssemblyFileVersion,
    string ReleaseChannel,
    string FrameworkDescription,
    string? ReleaseNotesPath,
    string? UpdateManifestPath);
