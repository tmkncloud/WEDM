using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

public sealed class ManagedServerDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("cluster")]
    public string? Cluster { get; set; }

    [JsonPropertyName("listenPort")]
    public int ListenPort { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "RUNNING";

    [JsonPropertyName("jvmArgsSummary")]
    public string? JvmArgsSummary { get; set; }
}

public sealed class ClusterDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("memberCount")]
    public int MemberCount { get; set; }
}

public sealed class ReportsServerDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class EnvironmentDiscoveryFinding
{
    [JsonPropertyName("category")]
    public CompatibilityRiskCategory Category { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public CompatibilitySeverity Severity { get; set; }
}
