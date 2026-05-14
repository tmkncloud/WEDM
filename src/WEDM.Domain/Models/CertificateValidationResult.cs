namespace WEDM.Domain.Models;

/// <summary>Outcome of keystore / certificate checks (passwords must never be echoed).</summary>
public sealed class CertificateValidationResult
{
    public bool   Success     { get; init; }
    public string Message     { get; init; } = string.Empty;
    public string Subject     { get; init; } = string.Empty;
    public DateTimeOffset? NotAfter { get; init; }
}
