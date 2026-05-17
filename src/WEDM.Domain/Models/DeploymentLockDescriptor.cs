namespace WEDM.Domain.Models;

/// <summary>
/// Machine-wide deployment lock metadata for a guarded Oracle resource.
///
/// Lock file format (v2):
///   A single <c>{sanitizedKey}.meta</c> file in the lock directory.
///   The file IS the lock — its existence means the resource is held.
///   Created via atomic provisional-then-rename protocol:
///     1. Write <c>{sanitizedKey}-{sessionId:N}.meta.tmp</c> (provisional, fully flushed)
///     2. <c>File.Move(provisional, meta, overwrite: false)</c> — atomic exclusive gate
///   Released by deleting the <c>.meta</c> file.
///
/// Backward compatibility:
///   v1 locks wrote both an empty <c>.lock</c> sentinel and a <c>.meta</c> data file.
///   <see cref="LockFileVersion"/> == 1 identifies these; reconciliation removes the orphaned
///   <c>.lock</c> file during startup.  The <c>.meta</c> content is otherwise identical.
/// </summary>
public sealed class DeploymentLockDescriptor
{
    /// <summary>
    /// Lock file format version.
    /// 1 = legacy (empty .lock + .meta pair).
    /// 2 = current (single .meta file, atomic provisional-rename protocol).
    /// </summary>
    public int LockFileVersion { get; set; } = 2;

    public string ResourceKey { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourcePath { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public Guid ConfigurationId { get; set; }
    public string MachineName { get; set; } = Environment.MachineName;
    public string OwnerUser { get; set; } = Environment.UserName;
    public int OwnerProcessId { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset LastHeartbeatAt { get; set; }
    public string DeploymentName { get; set; } = string.Empty;

    /// <summary>True when the heartbeat is older than <paramref name="maxAge"/>.</summary>
    public bool IsStale(TimeSpan maxAge)
        => DateTimeOffset.UtcNow - LastHeartbeatAt > maxAge;

    /// <summary>True when the lock was acquired by a different machine.</summary>
    public bool IsFromDifferentMachine
        => !string.Equals(MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
}
