using System.Text.Json;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Persistence;
using WEDM.Infrastructure.Security;

namespace WEDM.Infrastructure.Deployment;

/// <summary>
/// Atomic JSON session store for crash recovery, deployment resume, and operator diagnostics.
///
/// Secret handling — R-03 fix:
///   Old behavior: <c>DeploymentConfigurationSanitizer.RedactSecrets()</c> wrote
///   "***REDACTED***" to every credential field, making resumes permanently broken.
///
///   New behavior: <see cref="ISecretRehydrationService.PersistAndBind"/> is called on
///   the cloned config before serialization.  Each plaintext value is encrypted into the
///   DPAPI vault and replaced in the config clone with a sentinel of the form
///   "__WEDM_VAULT_REF:{alias}__".  The sentinel is safe to serialize to disk.
///   On resume, <see cref="ISecretRehydrationService.Rehydrate"/> reverses this —
///   sentinels are looked up in the vault and replaced with decrypted plaintext.
///
/// Fallback: when <paramref name="rehydration"/> is null (e.g. in tests or legacy callers),
///   the store falls back to the original redaction-based sanitizer to preserve backward
///   compatibility.  This fallback should never be reached in production after wiring.
/// </summary>
public sealed class JsonDeploymentSessionStore : IDeploymentSessionStore
{
    public const string SessionsSubdir = "sessions";
    public const string CorruptSubdir  = "corrupt";

    private readonly ISecretRehydrationService? _rehydration;

    public JsonDeploymentSessionStore(
        string? rootDirectory = null,
        ISecretRehydrationService? rehydration = null)
    {
        _rehydration = rehydration;
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WEDM", "deployments");
        Directory.CreateDirectory(Path.Combine(RootDirectory, SessionsSubdir));
        Directory.CreateDirectory(Path.Combine(RootDirectory, CorruptSubdir));
    }

    public string RootDirectory { get; }

    public async Task SaveAsync(DeploymentSessionState state, CancellationToken cancellationToken = default)
    {
        state.LastCheckpointAt = DateTimeOffset.UtcNow;
        state.SchemaVersion    = DeploymentSessionState.CurrentSchemaVersion;

        // Clone the configuration for safe serialization — the original live config
        // must retain its plaintext values so the running workflow can continue.
        var safeConfig = JsonSerializer.Deserialize<DeploymentConfiguration>(
            JsonSerializer.Serialize(state.Configuration, DeploymentJsonOptions.Create()),
            DeploymentJsonOptions.Create())!;

        if (_rehydration is not null)
        {
            // R-03 fix: vault each secret and replace with a recoverable sentinel.
            var bindings = _rehydration.PersistAndBind(safeConfig, state.SessionId);

            // Persist binding metadata alongside the session (no secret values).
            state.SecretReferences = bindings
                .Where(b => b.IsResolved && b.Source == SecretResolutionSource.DpapiVault)
                .Select(b => b.Reference)
                .ToList();
        }
        else
        {
            // Legacy fallback: redact secrets so no plaintext leaks to disk.
            // This path should not be reached in production after DI wiring.
            DeploymentConfigurationSanitizer.RedactSecrets(safeConfig);
        }

        state.Configuration = safeConfig;

        var path = SessionPath(state.SessionId);
        var json = JsonSerializer.Serialize(state, DeploymentJsonOptions.Create());
        await AtomicFileWriter.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentSessionState?> LoadAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var path = SessionPath(sessionId);
        if (!File.Exists(path)) return null;

        try
        {
            var json  = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<DeploymentSessionState>(json, DeploymentJsonOptions.Create());
            if (state is null)
                throw new InvalidDataException("Session file deserialized to null.");
            if (state.SchemaVersion > DeploymentSessionState.CurrentSchemaVersion)
                throw new NotSupportedException(
                    $"Session schema v{state.SchemaVersion} is newer than this WEDM build supports.");
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            QuarantineCorruptFile(path, sessionId);
            throw new InvalidDataException($"Deployment session '{sessionId}' is corrupt or unreadable.", ex);
        }
    }

    public async Task<IReadOnlyList<DeploymentSessionState>> ListRecoverableAsync(
        CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(RootDirectory, SessionsSubdir);
        if (!Directory.Exists(dir)) return [];

        var results = new List<DeploymentSessionState>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json  = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var state = JsonSerializer.Deserialize<DeploymentSessionState>(json, DeploymentJsonOptions.Create());
                if (state is null) continue;
                if (state.CanResume || state.LifecycleStatus == DeploymentLifecycleStatus.InProgress)
                    results.Add(state);
            }
            catch
            {
                // Skip unreadable entries in list view
            }
        }

        return results
            .OrderByDescending(s => s.LastCheckpointAt)
            .ToList();
    }

    public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var path = SessionPath(sessionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(SessionPath(sessionId)));

    private string SessionPath(Guid sessionId)
        => Path.Combine(RootDirectory, SessionsSubdir, $"{sessionId:N}.json");

    private void QuarantineCorruptFile(string path, Guid sessionId)
    {
        try
        {
            var dest = Path.Combine(
                RootDirectory, CorruptSubdir,
                $"{sessionId:N}-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            File.Move(path, dest, overwrite: true);
        }
        catch
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
