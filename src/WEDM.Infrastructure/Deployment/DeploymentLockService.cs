using System.Diagnostics;
using System.Text.Json;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Persistence;

namespace WEDM.Infrastructure.Deployment;

public sealed class DeploymentLockService : IDeploymentLockService
{
    public static readonly TimeSpan DefaultStaleLockAge = TimeSpan.FromHours(4);

    private readonly string _lockRoot;

    public DeploymentLockService(string? lockRoot = null)
    {
        _lockRoot = lockRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WEDM", "locks");
        Directory.CreateDirectory(_lockRoot);
    }

    public async Task<DeploymentLockAcquireResult> TryAcquireAsync(
        DeploymentConfiguration config,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await CleanupStaleLocksAsync(DefaultStaleLockAge, cancellationToken).ConfigureAwait(false);

        var resources = BuildResourceList(config);
        var acquired  = new List<DeploymentLockDescriptor>();
        var conflicts = new List<DeploymentLockDescriptor>();

        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await TryReadLockAsync(resource.Key, cancellationToken).ConfigureAwait(false);
            if (existing is not null && existing.SessionId != sessionId)
            {
                conflicts.Add(existing);
                continue;
            }

            var descriptor = new DeploymentLockDescriptor
            {
                ResourceKey     = resource.Key,
                ResourceType    = resource.Type,
                ResourcePath    = resource.Path,
                SessionId       = sessionId,
                ConfigurationId = config.Id,
                MachineName     = Environment.MachineName,
                OwnerUser       = Environment.UserName,
                OwnerProcessId  = Environment.ProcessId,
                AcquiredAt      = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                DeploymentName  = config.Name
            };

            var lockPath = LockFilePath(resource.Key);
            try
            {
                await using var fs = new FileStream(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                var json = JsonSerializer.Serialize(descriptor, DeploymentJsonOptions.Create());
                await AtomicFileWriter.WriteAllTextAsync(lockPath + ".meta", json, cancellationToken)
                    .ConfigureAwait(false);
                acquired.Add(descriptor);
            }
            catch (IOException)
            {
                var holder = await TryReadLockAsync(resource.Key, cancellationToken).ConfigureAwait(false);
                if (holder is not null) conflicts.Add(holder);
            }
        }

        if (conflicts.Count > 0)
        {
            foreach (var d in acquired)
                await TryReleaseResourceAsync(d.ResourceKey, sessionId).ConfigureAwait(false);

            return new DeploymentLockAcquireResult
            {
                Acquired         = false,
                FailureReason    = BuildConflictMessage(conflicts),
                ConflictingLocks = conflicts
            };
        }

        return new DeploymentLockAcquireResult
        {
            Acquired       = true,
            AcquiredLocks  = acquired
        };
    }

    public async Task ReleaseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        foreach (var meta in Directory.EnumerateFiles(_lockRoot, "*.meta"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false);
                var desc = JsonSerializer.Deserialize<DeploymentLockDescriptor>(json, DeploymentJsonOptions.Create());
                if (desc?.SessionId == sessionId)
                    await TryReleaseResourceAsync(desc.ResourceKey, sessionId).ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }
    }

    public async Task HeartbeatAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        foreach (var meta in Directory.EnumerateFiles(_lockRoot, "*.meta"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false);
                var desc = JsonSerializer.Deserialize<DeploymentLockDescriptor>(json, DeploymentJsonOptions.Create());
                if (desc?.SessionId != sessionId) continue;
                desc.LastHeartbeatAt = DateTimeOffset.UtcNow;
                await AtomicFileWriter.WriteAllTextAsync(meta, JsonSerializer.Serialize(desc, DeploymentJsonOptions.Create()), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }
    }

    public async Task<IReadOnlyList<DeploymentLockDescriptor>> ListActiveLocksAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<DeploymentLockDescriptor>();
        foreach (var meta in Directory.EnumerateFiles(_lockRoot, "*.meta"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false);
                var desc = JsonSerializer.Deserialize<DeploymentLockDescriptor>(json, DeploymentJsonOptions.Create());
                if (desc is not null) list.Add(desc);
            }
            catch { /* skip */ }
        }
        return list;
    }

    public async Task<int> CleanupStaleLocksAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var meta in Directory.EnumerateFiles(_lockRoot, "*.meta"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false);
                var desc = JsonSerializer.Deserialize<DeploymentLockDescriptor>(json, DeploymentJsonOptions.Create());
                if (desc is null || !desc.IsStale(maxAge)) continue;
                if (IsProcessAlive(desc.OwnerProcessId)) continue;
                await TryReleaseResourceAsync(desc.ResourceKey, desc.SessionId).ConfigureAwait(false);
                removed++;
            }
            catch { /* skip */ }
        }
        return removed;
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try { return Process.GetProcessById(pid) is not null; }
        catch { return false; }
    }

    private async Task<DeploymentLockDescriptor?> TryReadLockAsync(string resourceKey, CancellationToken ct)
    {
        var meta = LockFilePath(resourceKey) + ".meta";
        if (!File.Exists(meta)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(meta, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<DeploymentLockDescriptor>(json, DeploymentJsonOptions.Create());
        }
        catch { return null; }
    }

    private Task TryReleaseResourceAsync(string resourceKey, Guid sessionId)
    {
        var lockPath = LockFilePath(resourceKey);
        var metaPath = lockPath + ".meta";
        try
        {
            if (File.Exists(lockPath)) File.Delete(lockPath);
            if (File.Exists(metaPath)) File.Delete(metaPath);
        }
        catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private string LockFilePath(string resourceKey)
        => Path.Combine(_lockRoot, $"{SanitizeKey(resourceKey)}.lock");

    private static string SanitizeKey(string key)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            key = key.Replace(c, '_');
        return key.Length > 120 ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..64] : key;
    }

    private static List<(string Key, string Type, string Path)> BuildResourceList(DeploymentConfiguration config)
    {
        var list = new List<(string, string, string)>();
        if (!string.IsNullOrWhiteSpace(config.Paths.MiddlewareHome))
            list.Add(("mw:" + config.Paths.MiddlewareHome.ToLowerInvariant(), "OracleHome", config.Paths.MiddlewareHome));
        if (!string.IsNullOrWhiteSpace(config.Paths.OracleInventory))
            list.Add(("inv:" + config.Paths.OracleInventory.ToLowerInvariant(), "CentralInventory", config.Paths.OracleInventory));
        var domainHome = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);
        if (!string.IsNullOrWhiteSpace(domainHome))
            list.Add(("dom:" + domainHome.ToLowerInvariant(), "DomainHome", domainHome));
        return list;
    }

    private static string BuildConflictMessage(IReadOnlyList<DeploymentLockDescriptor> conflicts)
    {
        var first = conflicts[0];
        return $"Deployment lock held on {first.ResourceType} '{first.ResourcePath}' by {first.OwnerUser}@{first.MachineName} " +
               $"(session {first.SessionId:N}, PID {first.OwnerProcessId}, since {first.AcquiredAt:u}).";
    }
}
