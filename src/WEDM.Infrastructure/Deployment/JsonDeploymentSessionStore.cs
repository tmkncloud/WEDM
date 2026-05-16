using System.Text.Json;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Persistence;
using WEDM.Infrastructure.Security;

namespace WEDM.Infrastructure.Deployment;

public sealed class JsonDeploymentSessionStore : IDeploymentSessionStore
{
    public const string SessionsSubdir = "sessions";
    public const string CorruptSubdir  = "corrupt";

    public JsonDeploymentSessionStore(string? rootDirectory = null)
    {
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

        var safeConfig = JsonSerializer.Deserialize<DeploymentConfiguration>(
            JsonSerializer.Serialize(state.Configuration, DeploymentJsonOptions.Create()),
            DeploymentJsonOptions.Create())!;
        DeploymentConfigurationSanitizer.RedactSecrets(safeConfig);
        state.Configuration = safeConfig;

        var path = SessionPath(state.SessionId);
        var json = JsonSerializer.Serialize(state, DeploymentJsonOptions.Create());
        await AtomicFileWriter.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentSessionState?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var path = SessionPath(sessionId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<DeploymentSessionState>> ListRecoverableAsync(CancellationToken cancellationToken = default)
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
                // skip unreadable entries in list view
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
            var dest = Path.Combine(RootDirectory, CorruptSubdir, $"{sessionId:N}-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            File.Move(path, dest, overwrite: true);
        }
        catch
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
