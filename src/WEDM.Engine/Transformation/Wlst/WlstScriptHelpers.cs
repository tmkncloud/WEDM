using System.Text;

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

    public static void AppendOnlineConnect(StringBuilder sb, MigrationContext ctx)
    {
        sb.AppendLine($"connect('weblogic', '***CHANGE_PASSWORD***', 't3://{EscapePy(ctx.HostName)}:{ctx.AdminListenPort}')");
    }

  /// <summary>Begin an online configuration edit session (required for persistent domain changes).</summary>
    public static void AppendOnlineEditBegin(StringBuilder sb)
    {
        sb.AppendLine("edit()");
        sb.AppendLine("startEdit()");
    }

    public static void AppendOnlineEditCommit(StringBuilder sb)
    {
        sb.AppendLine("save()");
        sb.AppendLine("try:");
        sb.AppendLine("    activate(block='true')");
        sb.AppendLine("    print('[WEDM] Configuration activated successfully')");
        sb.AppendLine("except Exception, ex:");
        sb.AppendLine("    print('[WEDM] ACTIVATION FAILED: ' + str(ex))");
        sb.AppendLine("    cancelEdit('y')");
        sb.AppendLine("    raise");
    }

    public static void AppendOnlineDisconnect(StringBuilder sb)
    {
        sb.AppendLine("disconnect()");
        sb.AppendLine("exit()");
    }

    public static void AppendOnlineScriptFooter(StringBuilder sb, bool usedEditSession)
    {
        if (usedEditSession)
            AppendOnlineEditCommit(sb);
        AppendOnlineDisconnect(sb);
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
