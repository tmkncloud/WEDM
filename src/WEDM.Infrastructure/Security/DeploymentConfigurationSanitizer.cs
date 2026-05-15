using System.Text.Json;
using System.Text.Json.Serialization;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Security;

/// <summary>Produces configuration snapshots safe for logs, reports, and JSON export.</summary>
public static class DeploymentConfigurationSanitizer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToSafeJson(DeploymentConfiguration config)
    {
        var clone = JsonSerializer.Deserialize<DeploymentConfiguration>(
            JsonSerializer.Serialize(config, Options), Options)!;
        RedactSecrets(clone);
        return JsonSerializer.Serialize(clone, Options);
    }

    public static void RedactSecrets(DeploymentConfiguration config)
    {
        if (!string.IsNullOrEmpty(config.Domain.AdminPassword))
            config.Domain.AdminPassword = "***REDACTED***";
        if (!string.IsNullOrEmpty(config.Database.SysPassword))
            config.Database.SysPassword = "***REDACTED***";
        if (!string.IsNullOrEmpty(config.Database.SchemaPassword))
            config.Database.SchemaPassword = "***REDACTED***";
        if (!string.IsNullOrEmpty(config.Security.SslCertificates.IdentityKeystorePassword))
            config.Security.SslCertificates.IdentityKeystorePassword = "***REDACTED***";
        if (!string.IsNullOrEmpty(config.Security.SslCertificates.TrustKeystorePassword))
            config.Security.SslCertificates.TrustKeystorePassword = "***REDACTED***";
    }
}
