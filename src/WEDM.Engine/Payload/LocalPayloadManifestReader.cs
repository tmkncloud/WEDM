using System.Text.Json;
using WEDM.Domain.Models;

namespace WEDM.Engine.Payload;

public sealed class LocalPayloadManifestReader
{
    public const string ManifestFileName = "payload-manifest.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public PayloadManifestDocument? TryLoad(string versionFolderPath)
    {
        var path = Path.Combine(versionFolderPath, ManifestFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PayloadManifestDocument>(json, Options);
        }
        catch
        {
            return null;
        }
    }
}
