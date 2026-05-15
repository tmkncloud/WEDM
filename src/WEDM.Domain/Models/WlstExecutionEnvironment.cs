namespace WEDM.Domain.Models;

/// <summary>Oracle environment variables injected into WLST PowerShell execution.</summary>
public sealed class WlstExecutionEnvironment
{
    public string? JavaHome { get; init; }
    public string? OracleHome { get; init; }
}
