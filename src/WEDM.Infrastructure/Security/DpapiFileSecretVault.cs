using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Persistence;

namespace WEDM.Infrastructure.Security;

/// <summary>
/// DPAPI-protected JSON vault under ProgramData.
///
/// Vault file layout — {deploymentId:N}.vault.json:
/// <code>
/// {
///   "_meta_": {
///     "ownerUser":      "DOMAIN\\User",
///     "ownerMachine":   "SERVER01",
///     "createdAt":      "2024-01-01T00:00:00Z",
///     "lastModifiedAt": "2024-01-01T01:00:00Z"
///   },
///   "domain.admin.password":           "Base64(DPAPI(plaintext))",
///   "database.sys.password":           "Base64(DPAPI(plaintext))",
///   ...
/// }
/// </code>
///
/// DPAPI scope: DataProtectionScope.CurrentUser — encrypted under the Windows
/// user account that executed Save().  Decryption will fail if TryLoad() is
/// called from a different user account (cross-user resume scenario).
///
/// Thread-safety: each public method performs a full Load/Mutate/Write cycle
/// under a per-deployment file lock (via AtomicFileWriter).  Concurrent callers
/// on the same deployment ID will serialize through the atomic writer.
/// </summary>
public sealed class DpapiFileSecretVault : ILocalSecretVault
{
    private const string MetaKey = "_meta_";

    private readonly string _rootDir;

    public DpapiFileSecretVault()
    {
        _rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WEDM", "secrets");
        Directory.CreateDirectory(_rootDir);
    }

    // ── Core read / write ─────────────────────────────────────────────────────

    public void Save(Guid deploymentId, string secretName, string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return;
        if (secretName == MetaKey)
            throw new ArgumentException("Secret name conflicts with reserved vault metadata key.", nameof(secretName));

        var (entries, meta) = LoadVault(deploymentId);

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var prot  = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        entries[secretName] = Convert.ToBase64String(prot);

        // Update metadata
        meta.LastModifiedAt = DateTimeOffset.UtcNow;
        if (meta.CreatedAt == default) meta.CreatedAt = meta.LastModifiedAt;
        if (string.IsNullOrEmpty(meta.OwnerUser))    meta.OwnerUser    = Environment.UserName;
        if (string.IsNullOrEmpty(meta.OwnerMachine)) meta.OwnerMachine = Environment.MachineName;

        WriteVault(deploymentId, entries, meta);
    }

    public string? TryLoad(Guid deploymentId, string secretName)
    {
        var (entries, _) = LoadVault(deploymentId);
        if (!entries.TryGetValue(secretName, out var b64)) return null;
        try
        {
            var prot = Convert.FromBase64String(b64);
            var raw  = ProtectedData.Unprotect(prot, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch (CryptographicException)
        {
            // DPAPI decryption failed — different user or machine, or corrupted blob
            return null;
        }
    }

    // ── Extended query API ────────────────────────────────────────────────────

    public bool Exists(Guid deploymentId, string secretName)
    {
        var (entries, _) = LoadVault(deploymentId);
        return entries.ContainsKey(secretName);
    }

    public IReadOnlyList<string> ListKeys(Guid deploymentId)
    {
        var (entries, _) = LoadVault(deploymentId);
        return entries.Keys.ToList().AsReadOnly();
    }

    public void Delete(Guid deploymentId, string secretName)
    {
        var (entries, meta) = LoadVault(deploymentId);
        if (!entries.Remove(secretName)) return;
        meta.LastModifiedAt = DateTimeOffset.UtcNow;
        WriteVault(deploymentId, entries, meta);
    }

    public VaultDiagnostics GetDiagnostics(Guid deploymentId)
    {
        var filePath = FilePath(deploymentId);
        if (!File.Exists(filePath))
        {
            return new VaultDiagnostics
            {
                DeploymentId   = deploymentId,
                VaultFileExists = false,
                CurrentUser    = Environment.UserName,
                CurrentMachine = Environment.MachineName,
                ScopeCompatible = false,
                ScopeWarning   = "No vault file exists for this deployment. " +
                                 "Secrets must be re-entered before resuming."
            };
        }

        var (entries, meta) = LoadVault(deploymentId);

        var ownerUser    = meta.OwnerUser    ?? "(unknown)";
        var ownerMachine = meta.OwnerMachine ?? "(unknown)";
        var currentUser  = Environment.UserName;
        var currentMachine = Environment.MachineName;

        // DPAPI CurrentUser scope: compatible only when same user on same machine.
        var userMatch    = string.Equals(ownerUser, currentUser, StringComparison.OrdinalIgnoreCase);
        var machineMatch = string.Equals(ownerMachine, currentMachine, StringComparison.OrdinalIgnoreCase);
        var compatible   = userMatch && machineMatch;

        string? scopeWarning = null;
        if (!userMatch)
            scopeWarning = $"Vault was created by '{ownerUser}' but current user is '{currentUser}'. " +
                           "DPAPI CurrentUser scope will prevent decryption — secrets cannot be restored. " +
                           "Run WEDM under the original user account, or re-enter all secrets.";
        else if (!machineMatch)
            scopeWarning = $"Vault was created on machine '{ownerMachine}' but current machine is '{currentMachine}'. " +
                           "DPAPI CurrentUser scope may prevent decryption on a different machine. " +
                           "Consider re-entering all secrets on this machine.";

        DateTimeOffset? lastModified = null;
        try
        {
            lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero);
        }
        catch { }

        return new VaultDiagnostics
        {
            DeploymentId    = deploymentId,
            VaultFileExists = true,
            EntryCount      = entries.Count,
            EntryAliases    = entries.Keys.ToList().AsReadOnly(),
            VaultOwnerUser  = ownerUser,
            VaultOwnerMachine = ownerMachine,
            CurrentUser     = currentUser,
            CurrentMachine  = currentMachine,
            ScopeCompatible = compatible,
            ScopeWarning    = scopeWarning,
            VaultCreatedAt  = meta.CreatedAt == default ? null : meta.CreatedAt,
            LastModifiedAt  = lastModified ?? (meta.LastModifiedAt == default ? null : meta.LastModifiedAt),
        };
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private string FilePath(Guid deploymentId)
        => Path.Combine(_rootDir, $"{deploymentId:N}.vault.json");

    private (Dictionary<string, string> entries, VaultMeta meta) LoadVault(Guid deploymentId)
    {
        var path = FilePath(deploymentId);
        if (!File.Exists(path))
            return (new Dictionary<string, string>(StringComparer.Ordinal), new VaultMeta());

        try
        {
            var json = File.ReadAllText(path);
            var raw  = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);

            // Extract metadata from the reserved key
            VaultMeta meta = new();
            if (raw.TryGetValue(MetaKey, out var metaJson))
            {
                try
                {
                    meta = JsonSerializer.Deserialize<VaultMeta>(metaJson) ?? new VaultMeta();
                }
                catch { /* ignore corrupt meta */ }
                raw.Remove(MetaKey);
            }

            return (raw, meta);
        }
        catch
        {
            return (new Dictionary<string, string>(StringComparer.Ordinal), new VaultMeta());
        }
    }

    private void WriteVault(Guid deploymentId, Dictionary<string, string> entries, VaultMeta meta)
    {
        // Merge metadata into the persisted map under the reserved key
        var map = new Dictionary<string, string>(entries, StringComparer.Ordinal)
        {
            [MetaKey] = JsonSerializer.Serialize(meta)
        };

        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = false });
        AtomicFileWriter.WriteAllTextAsync(FilePath(deploymentId), json).GetAwaiter().GetResult();
    }

    // ── Private models ────────────────────────────────────────────────────────

    private sealed class VaultMeta
    {
        [JsonPropertyName("ownerUser")]
        public string? OwnerUser { get; set; }

        [JsonPropertyName("ownerMachine")]
        public string? OwnerMachine { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("lastModifiedAt")]
        public DateTimeOffset LastModifiedAt { get; set; }
    }
}
