using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

/// <summary>Where operational secrets are persisted beyond in-process configuration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecretsStorageMode
{
    /// <summary>Secrets stay in configuration; logging/reporting must redact them.</summary>
    ConfigOnlyRedactedLogging = 0,

    /// <summary>DPAPI-protected blobs under the local WEDM data directory (per machine user).</summary>
    DpapiLocalVault = 1,

    /// <summary>Reserved — Windows Credential Manager integration (enterprise follow-up).</summary>
    WindowsCredentialManager = 2
}

/// <summary>Operational secret handling and optional DPAPI vault persistence.</summary>
public sealed class SecretsManagementConfiguration
{
    [JsonPropertyName("preferredStorage")]
    public SecretsStorageMode PreferredStorage { get; set; } = SecretsStorageMode.ConfigOnlyRedactedLogging;

    /// <summary>When true, admin and DB passwords are also written to the DPAPI vault after validation (configuration retains copies for this session).</summary>
    [JsonPropertyName("persistToDpapiVaultAfterValidation")]
    public bool PersistToDpapiVaultAfterValidation { get; set; }

    [JsonPropertyName("localVaultRelativeDirectory")]
    public string LocalVaultRelativeDirectory { get; set; } = @"WEDM\secrets";
}

public enum KeystoreFormat
{
    Auto,
    Pkcs12,
    Jks
}

/// <summary>Custom identity/trust material for Node Manager and server SSL preparation.</summary>
public sealed class SslCertificateConfiguration
{
    [JsonPropertyName("identityKeystorePath")]
    public string IdentityKeystorePath { get; set; } = string.Empty;

    [JsonPropertyName("identityKeystorePassword")]
    public string IdentityKeystorePassword { get; set; } = string.Empty;

    [JsonPropertyName("trustKeystorePath")]
    public string TrustKeystorePath { get; set; } = string.Empty;

    [JsonPropertyName("trustKeystorePassword")]
    public string TrustKeystorePassword { get; set; } = string.Empty;

    [JsonPropertyName("identityPrivateKeyAlias")]
    public string IdentityPrivateKeyAlias { get; set; } = "server";

    [JsonPropertyName("keystoreFormat")]
    public KeystoreFormat KeystoreFormat { get; set; } = KeystoreFormat.Pkcs12;

    [JsonPropertyName("validateTrustChainOnDeploy")]
    public bool ValidateTrustChainOnDeploy { get; set; } = true;

    [JsonPropertyName("minimumCertificateValidityDays")]
    public int MinimumCertificateValidityDays { get; set; } = 30;

    /// <summary>When true and identity path is empty in DEV, deployment may generate a self-signed PFX under reports (non-prod only).</summary>
    [JsonPropertyName("generateDevSelfSignedIfMissing")]
    public bool GenerateDevSelfSignedIfMissing { get; set; }
}

/// <summary>Single compliance or security checklist finding.</summary>
public sealed class ComplianceFinding
{
    public string Id          { get; set; } = string.Empty;
    public string Category    { get; set; } = string.Empty;
    public string Title       { get; set; } = string.Empty;
    public string Detail      { get; set; } = string.Empty;
    public int    Weight      { get; set; } = 1;
    public bool   Passed      { get; set; }
}

/// <summary>Aggregated security / hardening / SSL readiness audit (no secret values).</summary>
public sealed class ComplianceReport
{
    public Guid                 ReportId          { get; init; } = Guid.NewGuid();
    public Guid                 ConfigurationId { get; set; }
    public string               MachineName     { get; set; } = global::System.Environment.MachineName;
    public DateTimeOffset       GeneratedAt     { get; set; } = DateTimeOffset.UtcNow;
    public DeploymentEnvironmentKind Environment { get; set; }

    public int OverallScore           { get; set; }
    public int HardeningScore         { get; set; }
    public int SecretsManagementScore { get; set; }
    public int SslReadinessScore      { get; set; }
    public int ProductionReadinessScore { get; set; }

    public List<ComplianceFinding> Findings { get; set; } = new();
}
