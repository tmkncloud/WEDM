namespace WEDM.Engine.Transformation.Wlst;

internal static class WlstScriptHelpers
{
    public static string PyRaw(string path)
        => "r'" + path.Replace("'", "\\'", StringComparison.Ordinal) + "'";

    public static string EscapePy(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    public static string Header(string title, MigrationContext ctx)
    {
        return $"""
            # WEDM-generated WLST — {title}
            # Source: {ctx.SourceRelease} → Target: {ctx.TargetRelease}
            # Strategy: {ctx.Strategy}
            # DO NOT execute automatically — review in migration workspace before cutover.
            print('WEDM: {EscapePy(title)}')
            """;
    }
}

public sealed class MigrationContext
{
    public required string SourceRelease { get; init; }
    public required string TargetRelease { get; init; }
    public required string Strategy { get; init; }
    public required string TargetMiddlewareHome { get; init; }
    public required string TargetDomainHome { get; init; }
    public required string DomainName { get; init; }
    public required string AdminServerName { get; init; }
    public required string HostName { get; init; }
    public int AdminListenPort { get; init; }
}
