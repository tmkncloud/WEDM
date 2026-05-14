using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

/// <summary>OPatch / PSU automation configuration.</summary>
public sealed class PatchConfiguration
{
    /// <summary>When true, OPatch workflow steps are included in the deployment plan.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// When true, the workflow engine returns a patch-only step plan
    /// (middleware home must already exist with OPatch).
    /// </summary>
    [JsonPropertyName("standalonePatchWorkflow")]
    public bool StandalonePatchWorkflow { get; set; }

    /// <summary>Directory containing patch folders (each with patch.xml) or a single patch directory.</summary>
    [JsonPropertyName("patchStagingDirectory")]
    public string PatchStagingDirectory { get; set; } = string.Empty;

    /// <summary>Use <c>opatch napply</c> for the staging directory; when false, applies each immediate child patch sequentially.</summary>
    [JsonPropertyName("useNapply")]
    public bool UseNapply { get; set; } = true;

    [JsonPropertyName("opatchTimeoutMinutes")]
    public int OpatchTimeoutMinutes { get; set; } = 180;

    [JsonPropertyName("checkForRunningMiddlewareProcesses")]
    public bool CheckForRunningMiddlewareProcesses { get; set; } = true;

    [JsonPropertyName("runConflictPrerequisites")]
    public bool RunConflictPrerequisites { get; set; } = true;

    [JsonPropertyName("captureInventorySnapshots")]
    public bool CaptureInventorySnapshots { get; set; } = true;

    /// <summary>Optional explicit path to opatch.bat; when empty, resolved under ORACLE_HOME.</summary>
    [JsonPropertyName("opatchBatPathOverride")]
    public string OpatchBatPathOverride { get; set; } = string.Empty;
}

/// <summary>Structured output for patch compliance reporting.</summary>
public sealed class PatchReport
{
    public Guid                 ReportId        { get; init; } = Guid.NewGuid();
    public Guid                 ConfigurationId { get; set; }
    public string               MachineName     { get; set; } = Environment.MachineName;
    public DateTimeOffset       GeneratedAt     { get; set; } = DateTimeOffset.UtcNow;
    public string               OracleHome      { get; set; } = string.Empty;
    public string               OpatchVersion   { get; set; } = string.Empty;
    public string               StagingPath     { get; set; } = string.Empty;
    public List<AppliedPatchRecord> PatchesBefore { get; set; } = new();
    public List<AppliedPatchRecord> PatchesAfter  { get; set; } = new();
    public List<string>         StagingValidationNotes { get; set; } = new();
    public string               PreInventoryPath  { get; set; } = string.Empty;
    public string               PostInventoryPath { get; set; } = string.Empty;
    public string               MetadataSnapshotPath { get; set; } = string.Empty;
    public bool                 ApplySucceeded  { get; set; }
    public string               ApplyLogSummary { get; set; } = string.Empty;
}

public sealed class AppliedPatchRecord
{
    public string PatchId   { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? AppliedOn   { get; set; }
}

/// <summary>Result of patch-environment checks plus optional live <c>opatch version</c> output.</summary>
public sealed class PatchReadinessResult
{
    public required PrerequisiteValidationResult Validation { get; init; }

    public string? OpatchVersionOutput { get; init; }

    public int OpatchVersionExitCode { get; init; }
}
