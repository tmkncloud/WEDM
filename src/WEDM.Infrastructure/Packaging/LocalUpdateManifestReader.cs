using System.Text.Json;
using System.Text.Json.Serialization;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Packaging;

public sealed class LocalUpdateManifestReader : IUpdateManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<WedmUpdateManifest?> TryReadLocalAsync(string manifestFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestFilePath) || !File.Exists(manifestFilePath))
            return null;

        await using var stream = File.OpenRead(manifestFilePath);
        return await JsonSerializer.DeserializeAsync<WedmUpdateManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
