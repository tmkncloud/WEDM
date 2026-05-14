using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// In-process state shared between OPatch workflow steps for reporting (singleton).
/// </summary>
public interface IPatchExecutionState
{
    void BeginSession(Guid configurationId);

    void Clear();

    string? OpatchVersionOutput { get; set; }

    string? PreInventoryRaw  { get; set; }
    string? PostInventoryRaw { get; set; }

    string? PreInventoryFilePath  { get; set; }
    string? PostInventoryFilePath { get; set; }

    string? MetadataSnapshotPath { get; set; }

    List<AppliedPatchRecord> ParsedPrePatches  { get; set; }
    List<AppliedPatchRecord> ParsedPostPatches { get; set; }

    List<string> StagingNotes { get; set; }

    string? LastApplyStdout { get; set; }

    int LastApplyExitCode { get; set; }
}
