using System.Text.Json;
using System.Text.Json.Serialization;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Migration;

/// <summary>Version-safe JSON options for migration session persistence and reporting.</summary>
public static class MigrationJsonOptions
{
    public static JsonSerializerOptions Create() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
