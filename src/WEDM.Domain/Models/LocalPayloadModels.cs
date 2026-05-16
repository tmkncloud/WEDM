using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

/// <summary>Defines one subfolder under D:\WEDM\{version}\ and how to match installers inside it.</summary>
public sealed class LocalPayloadFolderDefinition
{
    public LocalPayloadComponent Component { get; init; }
    public string FolderName { get; init; } = string.Empty;
    public IReadOnlyList<string> FilePatterns { get; init; } = [];
    public bool Required { get; init; } = true;
    /// <summary>When true, validation checks directory structure instead of a single file (e.g. webutil).</summary>
    public bool DirectoryPayload { get; init; }
    public IReadOnlyList<string> RequiredSubfolders { get; init; } = [];
}

public enum PayloadChecksumStatus
{
    NotChecked = 0,
    ManifestMissing = 1,
    Verified = 2,
    Mismatch = 3,
    WarningSkipped = 4
}

public sealed class LocalPayloadResolutionSnapshot
{
    public bool UsedLocalRepository { get; set; }
    public string RepositoryRoot { get; set; } = string.Empty;
    public string VersionFolder { get; set; } = string.Empty;
    public bool ManifestPresent { get; set; }
    public List<ResolvedLocalPayloadEntry> Entries { get; set; } = [];
}

public sealed class ResolvedLocalPayloadEntry
{
    public LocalPayloadComponent Component { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public string? ResolvedPath { get; set; }
    public IReadOnlyList<string> MatchedPatterns { get; set; } = [];
    public bool Found { get; set; }
    public bool Required { get; set; }
    public PayloadChecksumStatus ChecksumStatus { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ActualSha256 { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class LocalPayloadRepositoryReport
{
    public bool CanProceed { get; set; }
    public string RepositoryRoot { get; set; } = string.Empty;
    public string VersionFolder { get; set; } = string.Empty;
    public bool ManifestPresent { get; set; }
    public List<ResolvedLocalPayloadEntry> Entries { get; set; } = [];
    public List<LocalPayloadValidationFinding> Findings { get; set; } = [];
}

public sealed class LocalPayloadValidationFinding
{
    public string Code { get; set; } = string.Empty;
    public ValidationSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Remediation { get; set; } = string.Empty;
    public string? MissingPath { get; set; }
    public string? ExpectedPatterns { get; set; }
}

public sealed class PayloadManifestDocument
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("payloads")]
    public Dictionary<string, PayloadManifestEntry> Payloads { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PayloadManifestEntry
{
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}
