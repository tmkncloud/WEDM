namespace WEDM.Domain.Models;

/// <summary>Result of pre-flight validation for migration environment discovery paths.</summary>
public sealed class DiscoveryPathValidationResult
{
    public bool IsValid { get; init; }
    public string MiddlewareHomeError { get; init; } = string.Empty;
    public string DomainHomeError { get; init; } = string.Empty;
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static DiscoveryPathValidationResult Success() => new() { IsValid = true };
}
