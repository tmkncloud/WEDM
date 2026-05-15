using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

/// <summary>Controls prerequisite installer detection, download, and caching.</summary>
public sealed class PayloadAcquisitionConfiguration
{
    [JsonPropertyName("autoDownloadMissing")]
    public bool AutoDownloadMissing { get; set; } = true;

    [JsonPropertyName("skipInstallWhenPresent")]
    public bool SkipInstallWhenPresent { get; set; } = true;

    [JsonPropertyName("cacheDirectory")]
    public string CacheDirectory { get; set; } = @"C:\Oracle\WEDM\payloads";

    [JsonPropertyName("preferTemurinForJdk")]
    public bool PreferTemurinForJdk { get; set; } = true;

    [JsonPropertyName("validateChecksums")]
    public bool ValidateChecksums { get; set; } = true;
}
