namespace WEDM.Domain.Models;

/// <summary>Machine-wide deployment lock metadata for a guarded Oracle resource.</summary>
public sealed class DeploymentLockDescriptor
{
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

    public bool IsStale(TimeSpan maxAge)
        => DateTimeOffset.UtcNow - LastHeartbeatAt > maxAge;
}
