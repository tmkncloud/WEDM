using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

/// <summary>Reusable lifecycle profile (DEV / SIT / UAT / PROD) for defaults.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeploymentEnvironmentKind
{
    Dev,
    Sit,
    Uat,
    Prod
}

/// <summary>Production-oriented domain and Node Manager hardening options.</summary>
public sealed class DomainHardeningConfiguration
{
    [JsonPropertyName("productionMode")]
    public bool ProductionMode { get; set; }

    [JsonPropertyName("enableAdministrationPort")]
    public bool EnableAdministrationPort { get; set; }

    [JsonPropertyName("requireSecureListenAddresses")]
    public bool RequireSecureListenAddresses { get; set; }

    [JsonPropertyName("sslPreparationOnly")]
    public bool SslPreparationOnly { get; set; }

    [JsonPropertyName("strictPostValidation")]
    public bool StrictPostValidation { get; set; }
}

/// <summary>WLST online automation after AdminServer is reachable (nmEnroll, hardening, validation).</summary>
public sealed class DomainOnlineAutomationConfiguration
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("startAdminServerIfNotRunning")]
    public bool StartAdminServerIfNotRunning { get; set; } = true;

    [JsonPropertyName("runNmEnroll")]
    public bool RunNmEnroll { get; set; } = true;

    [JsonPropertyName("applyOnlineProductionAndMachineMapping")]
    public bool ApplyOnlineProductionAndMachineMapping { get; set; } = true;

    [JsonPropertyName("timeoutMinutes")]
    public int TimeoutMinutes { get; set; } = 45;

    [JsonPropertyName("adminStartupPollSeconds")]
    public int AdminStartupPollSeconds { get; set; } = 5;
}
