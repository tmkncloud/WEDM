using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Machine-wide mutex for Oracle Home, domain, and central inventory paths.</summary>
public interface IDeploymentLockService
{
    Task<DeploymentLockAcquireResult> TryAcquireAsync(
        DeploymentConfiguration config,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task HeartbeatAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeploymentLockDescriptor>> ListActiveLocksAsync(CancellationToken cancellationToken = default);

    Task<int> CleanupStaleLocksAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform a full lock-directory reconciliation: remove orphaned provisional files,
    /// legacy v1 <c>.lock</c> sentinels, stale and corrupt <c>.meta</c> files, and
    /// cross-machine abandonments.  Returns a structured report of every action taken.
    /// Should be called once on application startup before the first <see cref="TryAcquireAsync"/>.
    /// </summary>
    Task<LockReconciliationReport> ReconcileAsync(CancellationToken cancellationToken = default);
}

// ── Result types ──────────────────────────────────────────────────────────────

public sealed class DeploymentLockAcquireResult
{
    public bool Acquired { get; init; }
    public string? FailureReason { get; init; }
    public IReadOnlyList<DeploymentLockDescriptor> ConflictingLocks { get; init; } = [];
    public IReadOnlyList<DeploymentLockDescriptor> AcquiredLocks { get; init; } = [];
}

// ── Reconciliation diagnostics ────────────────────────────────────────────────

/// <summary>
/// What action the reconciler took on a given lock file during a reconciliation pass.
/// </summary>
public enum LockReconciliationAction
{
    /// <summary>File is valid and currently held — no action taken.</summary>
    Kept = 0,

    /// <summary>File removed because the heartbeat exceeded the stale threshold and the owning process is gone.</summary>
    RemovedStale = 1,

    /// <summary>File removed because the JSON content was invalid or could not be parsed.</summary>
    RemovedCorrupt = 2,

    /// <summary>
    /// Orphaned provisional file (<c>.meta.tmp</c>) removed.
    /// Left by a process that crashed after writing the provisional but before the atomic rename.
    /// </summary>
    RemovedOrphanedProvisional = 3,

    /// <summary>
    /// Legacy v1 empty <c>.lock</c> sentinel removed.
    /// These files have no metadata and were written by the pre-R02 lock protocol.
    /// </summary>
    RemovedLegacySentinel = 4,

    /// <summary>
    /// File removed because the owning machine name does not match the current machine and
    /// the heartbeat has expired.  The remote process is presumed gone.
    /// </summary>
    RemovedCrossMachineAbandoned = 5,
}

/// <summary>Diagnostics for a single file examined or acted upon during reconciliation.</summary>
public sealed class LockDiagnosticRecord
{
    /// <summary>Absolute path of the file examined.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Resource key extracted from the file name (empty for legacy sentinels with no readable metadata).</summary>
    public string ResourceKey { get; init; } = string.Empty;

    /// <summary>What the reconciler did with this file.</summary>
    public LockReconciliationAction Action { get; init; }

    /// <summary>Human-readable explanation of why this action was taken.</summary>
    public string Reason { get; init; } = string.Empty;

    // ── Owner metadata (null for corrupt / legacy files) ──────────────────────

    public string? OwnerUser { get; init; }
    public int?    OwnerPid { get; init; }
    public string? OwnerMachine { get; init; }
    public Guid?   OwnerSessionId { get; init; }
    public DateTimeOffset? AcquiredAt { get; init; }

    /// <summary>Age of the last heartbeat at the time this record was examined.</summary>
    public TimeSpan? HeartbeatAge { get; init; }
}

/// <summary>Structured output from a full lock-directory reconciliation pass.</summary>
public sealed record LockReconciliationReport
{
    public DateTimeOffset RanAt { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan Duration { get; set; }

    public int StaleLocksRemoved              { get; set; }
    public int CorruptMetaFilesRemoved        { get; set; }
    public int OrphanedProvisionalsRemoved    { get; set; }
    public int LegacySentinelFilesRemoved     { get; set; }
    public int CrossMachineAbandonedRemoved   { get; set; }
    public int ActiveLocksKept                { get; set; }

    public int TotalActionsCount =>
        StaleLocksRemoved + CorruptMetaFilesRemoved + OrphanedProvisionalsRemoved +
        LegacySentinelFilesRemoved + CrossMachineAbandonedRemoved;

    public IReadOnlyList<LockDiagnosticRecord> Records { get; init; } = [];
}
