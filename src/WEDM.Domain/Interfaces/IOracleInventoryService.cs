using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Manages the Oracle Central Inventory lifecycle for a deployment session.
///
/// Responsibilities:
///   • Read and parse ContentsXML/inventory.xml
///   • Detect registered Oracle homes (including stale/orphaned entries)
///   • Detect active inventory lock files
///   • Backup inventory.xml before any mutation
///   • Surgically remove home entries without corrupting the XML
///   • Validate pre-install and post-install inventory state
///   • Log all inventory operations with structured diagnostics
///
/// This service is the single authority for all Oracle Central Inventory interactions.
/// No step executor or rollback executor should read/write inventory.xml directly.
/// </summary>
public interface IOracleInventoryService
{
    // ── Read operations ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses the Oracle Central Inventory XML at the given inventory directory.
    /// Returns null when the inventory directory or inventory.xml does not exist.
    /// Never throws — parse errors are captured in <see cref="OracleInventorySnapshot.InventoryWarning"/>.
    /// </summary>
    OracleInventorySnapshot? ReadSnapshot(string oracleInventoryPath);

    /// <summary>
    /// Resolves the inventory.xml path inside the given inventory directory.
    /// Returns null when inventory.xml cannot be found.
    /// </summary>
    string? ResolveInventoryXmlPath(string oracleInventoryPath);

    // ── Detection ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the given <paramref name="middlewareHome"/> path appears in
    /// the Central Inventory at <paramref name="oracleInventoryPath"/>.
    /// Path comparison is case-insensitive and normalised (trailing slashes removed).
    /// </summary>
    bool IsHomeRegistered(string middlewareHome, string oracleInventoryPath);

    /// <summary>
    /// Determines the current state of <paramref name="middlewareHome"/> with respect to
    /// both the filesystem and the Central Inventory.
    /// </summary>
    OracleHomeState DetectHomeState(string middlewareHome, string oracleInventoryPath);

    /// <summary>
    /// Returns all homes that are registered in the Central Inventory but whose
    /// target directories no longer exist (orphaned/stale registrations).
    /// </summary>
    IReadOnlyList<OracleHomeDescriptor> FindOrphanedHomes(string oracleInventoryPath);

    /// <summary>
    /// Returns true when the given <paramref name="middlewareHome"/> directory exists
    /// but has an incomplete Oracle installation structure (i.e. partial/failed OUI).
    /// Checks for the presence of key marker directories: wlserver/, oracle_common/, inventory/.
    /// </summary>
    bool IsPartialInstall(string middlewareHome);

    /// <summary>
    /// Detects any active Oracle inventory lock files inside the inventory directory.
    /// Returns an empty list when the inventory is not locked.
    /// </summary>
    IReadOnlyList<OracleInventoryLockDescriptor> DetectLocks(string oracleInventoryPath);

    // ── Pre-install validation ────────────────────────────────────────────────

    /// <summary>
    /// Validates the Oracle inventory state before launching OUI.
    /// Checks that:
    ///   1. The target home is NOT already registered
    ///   2. No inventory lock is active
    ///   3. No partial or orphaned install exists at the target path
    ///
    /// Returns a <see cref="OracleInventoryValidationResult"/> with <c>CanProceed=true</c>
    /// when the inventory is clean and OUI may be safely launched.
    /// </summary>
    OracleInventoryValidationResult ValidateForInstall(
        string middlewareHome,
        string oracleInventoryPath);

    // ── Post-install validation ───────────────────────────────────────────────

    /// <summary>
    /// Validates the Oracle inventory state after a successful OUI exit.
    /// Checks that:
    ///   1. The target home IS now registered in the inventory
    ///   2. The inventory.xml is parseable
    ///   3. The target home directory structure is complete
    ///
    /// Returns a <see cref="OracleInventoryValidationResult"/> with <c>CanProceed=true</c>
    /// when the post-install state is consistent.
    /// </summary>
    OracleInventoryValidationResult ValidateAfterInstall(
        string middlewareHome,
        string oracleInventoryPath);

    // ── Mutation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a timestamped backup of inventory.xml before any mutation.
    /// Returns the backup file path, or null when the inventory.xml does not exist.
    /// </summary>
    string? BackupInventoryXml(string oracleInventoryPath);

    /// <summary>
    /// Surgically removes all HOME and COMPOSITEHOME elements whose LOC attribute
    /// matches <paramref name="middlewareHome"/> (case-insensitive) from inventory.xml.
    ///
    /// Sequence:
    ///   1. Snapshot inventory state before removal
    ///   2. Create backup of inventory.xml
    ///   3. Load and mutate the XML (remove matching HOME elements)
    ///   4. Write updated XML back atomically
    ///   5. Snapshot inventory state after removal
    ///   6. Return full audit trail in <see cref="OracleInventoryRemovalResult"/>
    ///
    /// Never throws — errors are captured in <see cref="OracleInventoryRemovalResult.Error"/>.
    /// </summary>
    OracleInventoryRemovalResult RemoveHomeEntry(
        string middlewareHome,
        string oracleInventoryPath);
}
