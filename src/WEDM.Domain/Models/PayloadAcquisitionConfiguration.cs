using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

/// <summary>Controls prerequisite installer detection, download, and caching.</summary>
public sealed class PayloadAcquisitionConfiguration
{
    /// <summary>When true, WEDM uses only the local repository under <see cref="DeploymentConfiguration.PayloadBasePath"/> (no downloads).</summary>
    [JsonPropertyName("useLocalRepositoryOnly")]
    public bool UseLocalRepositoryOnly { get; set; } = true;

    [JsonPropertyName("autoDownloadMissing")]
    public bool AutoDownloadMissing { get; set; } = false;

    [JsonPropertyName("skipInstallWhenPresent")]
    public bool SkipInstallWhenPresent { get; set; } = true;

    [JsonPropertyName("cacheDirectory")]
    public string CacheDirectory { get; set; } = @"C:\Oracle\WEDM\payloads";

    [JsonPropertyName("preferTemurinForJdk")]
    public bool PreferTemurinForJdk { get; set; } = true;

    [JsonPropertyName("validateChecksums")]
    public bool ValidateChecksums { get; set; } = true;
}
