using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WEDM.Domain.Interfaces;

namespace WEDM.Infrastructure.Security;

/// <summary>DPAPI-protected JSON vault under ProgramData (per deployment id + secret name).</summary>
public sealed class DpapiFileSecretVault : ILocalSecretVault
{
    private readonly string _rootDir;

    public DpapiFileSecretVault()
    {
        _rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WEDM", "secrets");
        Directory.CreateDirectory(_rootDir);
    }

    public void Save(Guid deploymentId, string secretName, string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return;
        var map = LoadMap(deploymentId);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var prot  = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        map[secretName] = Convert.ToBase64String(prot);
        WriteMap(deploymentId, map);
    }

    public string? TryLoad(Guid deploymentId, string secretName)
    {
        var map = LoadMap(deploymentId);
        if (!map.TryGetValue(secretName, out var b64)) return null;
        var prot = Convert.FromBase64String(b64);
        var raw  = ProtectedData.Unprotect(prot, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(raw);
    }

    private string FilePath(Guid deploymentId)
        => Path.Combine(_rootDir, $"{deploymentId:N}.vault.json");

    private Dictionary<string, string> LoadMap(Guid deploymentId)
    {
        var p = FilePath(deploymentId);
        if (!File.Exists(p)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var json = File.ReadAllText(p);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void WriteMap(Guid deploymentId, Dictionary<string, string> map)
    {
        var p = FilePath(deploymentId);
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(p, json);
    }
}
