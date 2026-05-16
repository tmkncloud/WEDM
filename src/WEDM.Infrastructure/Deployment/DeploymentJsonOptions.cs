using System.Text.Json;
using System.Text.Json.Serialization;

namespace WEDM.Infrastructure.Deployment;

public static class DeploymentJsonOptions
{
    public static JsonSerializerOptions Create() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
