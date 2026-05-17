using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

// ── Enums ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Describes the observed state of a target Oracle Home path with respect to
/// the Oracle Central Inventory and the local filesystem.
/// </summary>
public enum OracleHomeState
{
    /// <summary>Not registered in inventory and folder does not exist — safe to install.</summary>
    Clean = 0,

    /// <summary>Registered in inventory AND directory structure is complete — already installed.</summary>
    RegisteredAndPresent = 1,

    /// <summary>Registered in inventory but target folder is absent — stale orphaned entry.</summary>
    RegisteredOrphaned = 2,

    /// <summary>
    /// Target folder exists and appears partially populated (missing key subdirectories
    /// such as wlserver/, oracle_common/, or modules/), but is NOT in the inventory.
    /// Indicates a previous failed or interrupted OUI session.
    /// </summary>
    PartialInstall = 3,

    /// <summary>
    /// Target folder exists and contains enough structure to suggest a full install,
    /// but is NOT registered in the Central Inventory — unusual state.
    /// </summary>
    UnregisteredInstall = 4,

    /// <summary>A lock file exists inside the inventory directory — active OUI/OPatch operation.</summary>
    InventoryLocked = 5,

    /// <summary>State cannot be determined (e.g., inventory.xml is corrupt or unreadable).</summary>
    Unknown = 99,
}

/// <summary>
/// Observed state of the Oracle Central Inventory (oraInventory / ContentsXML/inventory.xml).
/// Distinct from <see cref="OracleHomeState"/>, which describes a single middleware home path.
/// </summary>
public enum OracleCentralInventoryState
{
    /// <summary>Central inventory.xml is absent — installation cannot proceed safely.</summary>
    Missing = 0,

    /// <summary>inventory.xml exists but is malformed or unreadable.</summary>
    Corrupted = 1,

    /// <summary>Valid inventory.xml with an empty HOME_LIST — clean-install bootstrap.</summary>
    Empty = 2,

    /// <summary>Valid inventory with registered homes that all resolve on disk.</summary>
    Healthy = 3,

    /// <summary>Valid inventory where every registered home path is missing on disk.</summary>
    Stale = 4,

    /// <summary>Active OUI lock files are present under the inventory directory.</summary>
    Locked = 5,

    /// <summary>Valid inventory with a mix of present and missing registered home paths.</summary>
    Partial = 6,
}

// ── Validation results ────────────────────────────────────────────────────────

/// <summary>
/// Result of an Oracle inventory pre-install or post-install validation check.
/// </summary>
public sealed class OracleInventoryValidationResult
{
    /// <summary>True when the inventory state allows OUI installation to proceed safely.</summary>
    public bool CanProceed { get; init; }

    /// <summary>Observed state of the target Oracle Home path.</summary>
    public OracleHomeState HomeState { get; init; } = OracleHomeState.Unknown;

    /// <summary>Absolute path of the target middleware home being validated.</summary>
    public string TargetMiddlewareHome { get; init; } = string.Empty;

    /// <summary>Absolute path of the Oracle Central Inventory directory.</summary>
    public string OracleInventoryPath { get; init; } = string.Empty;

    /// <summary>Inventory snapshot captured during validation (null if inventory unreadable).</summary>
    public OracleInventorySnapshot? Snapshot { get; init; }

    /// <summary>Human-readable summary of each check performed.</summary>
    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>Operator-actionable remediation steps when <see cref="CanProceed"/> is false.</summary>
    public IReadOnlyList<string> RemediationSteps { get; init; } = [];

    /// <summary>True when an inventory lock file was detected.</summary>
    public bool IsLocked { get; init; }

    /// <summary>
    /// Homes registered in the inventory that reference directories which no longer exist
    /// (stale/orphaned registrations that should be cleaned up).
    /// </summary>
    public IReadOnlyList<OracleHomeDescriptor> OrphanedHomes { get; init; } = [];

    /// <summary>
    /// Homes that appear in the inventory at the exact target path or as conflicting entries.
    /// </summary>
    public IReadOnlyList<OracleHomeDescriptor> ConflictingHomes { get; init; } = [];
}

// ── Removal results ───────────────────────────────────────────────────────────

/// <summary>
/// Result of an Oracle inventory home entry removal operation.
/// Captures before/after snapshots to support audit and rollback verification.
/// </summary>
public sealed class OracleInventoryRemovalResult
{
    /// <summary>True when the removal completed without error.</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable summary of the operation.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Path to the inventory.xml backup created before mutation (null if not created).</summary>
    public string? BackupPath { get; init; }

    /// <summary>Path to the inventory.xml that was mutated.</summary>
    public string? InventoryXmlPath { get; init; }

    /// <summary>Inventory snapshot captured immediately before the removal.</summary>
    public OracleInventorySnapshot? SnapshotBefore { get; init; }

    /// <summary>Inventory snapshot captured immediately after the removal.</summary>
    public OracleInventorySnapshot? SnapshotAfter { get; init; }

    /// <summary>True when the target home was actually found and removed from inventory.</summary>
    public bool HomeWasRegistered { get; init; }

    /// <summary>XML element names/LOC values of the removed entries.</summary>
    public IReadOnlyList<string> RemovedEntries { get; init; } = [];

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    // ── Factories ─────────────────────────────────────────────────────────────

    public static OracleInventoryRemovalResult NotFound(string inventoryPath) =>
        new()
        {
            Success             = true,
            HomeWasRegistered   = false,
            InventoryXmlPath    = inventoryPath,
            Message             = "Target home was not registered in the Oracle Central Inventory — nothing to remove.",
        };

    public static OracleInventoryRemovalResult Removed(
        string inventoryPath,
        string? backupPath,
        IReadOnlyList<string> removed,
        OracleInventorySnapshot before,
        OracleInventorySnapshot after) =>
        new()
        {
            Success           = true,
            HomeWasRegistered = true,
            InventoryXmlPath  = inventoryPath,
            BackupPath        = backupPath,
            RemovedEntries    = removed,
            SnapshotBefore    = before,
            SnapshotAfter     = after,
            Message           = $"Removed {removed.Count} home registration(s) from Central Inventory.",
        };

    public static OracleInventoryRemovalResult Failed(string error, string? inventoryPath = null) =>
        new()
        {
            Success          = false,
            Error            = error,
            InventoryXmlPath = inventoryPath,
            Message          = $"Inventory removal failed: {error}",
        };
}

// ── Lock descriptor ───────────────────────────────────────────────────────────

/// <summary>Describes an active Oracle inventory lock file.</summary>
public sealed class OracleInventoryLockDescriptor
{
    public string LockFilePath { get; init; } = string.Empty;
    public DateTimeOffset? LastModified { get; init; }
    public bool IsStale { get; init; }
}
