using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Patching;

/// <summary>Thread-safe OPatch session state for multi-step reporting.</summary>
public sealed class PatchExecutionState : IPatchExecutionState
{
    private readonly object _lock = new();

    public void BeginSession(Guid configurationId)
    {
        lock (_lock) ClearUnsafe();
    }

    public void Clear()
    {
        lock (_lock) ClearUnsafe();
    }

    private void ClearUnsafe()
    {
        OpatchVersionOutput      = null;
        PreInventoryRaw          = null;
        PostInventoryRaw         = null;
        PreInventoryFilePath     = null;
        PostInventoryFilePath    = null;
        MetadataSnapshotPath     = null;
        ParsedPrePatches         = new List<AppliedPatchRecord>();
        ParsedPostPatches        = new List<AppliedPatchRecord>();
        StagingNotes             = new List<string>();
        LastApplyStdout          = null;
        LastApplyExitCode        = 0;
    }

    public string? OpatchVersionOutput { get; set; }

    public string? PreInventoryRaw  { get; set; }
    public string? PostInventoryRaw { get; set; }

    public string? PreInventoryFilePath  { get; set; }
    public string? PostInventoryFilePath { get; set; }

    public string? MetadataSnapshotPath { get; set; }

    public List<AppliedPatchRecord> ParsedPrePatches  { get; set; } = new();
    public List<AppliedPatchRecord> ParsedPostPatches { get; set; } = new();

    public List<string> StagingNotes { get; set; } = new();

    public string? LastApplyStdout { get; set; }

    public int LastApplyExitCode { get; set; }
}
