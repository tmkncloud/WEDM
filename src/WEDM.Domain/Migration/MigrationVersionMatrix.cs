using WEDM.Domain.Enums;

namespace WEDM.Domain.Migration;

/// <summary>
/// Enterprise upgrade path matrix for Oracle Forms / middleware releases.
/// Target lists are filtered reactively in the UI based on the selected source release.
/// </summary>
public static class MigrationVersionMatrix
{
    private static readonly IReadOnlyDictionary<MiddlewareReleaseKind, MiddlewareReleaseKind[]> AllowedTargets =
        new Dictionary<MiddlewareReleaseKind, MiddlewareReleaseKind[]>
        {
            [MiddlewareReleaseKind.Forms6i]   = [MiddlewareReleaseKind.Forms10g, MiddlewareReleaseKind.Forms11g, MiddlewareReleaseKind.Forms12c, MiddlewareReleaseKind.Forms14c],
            [MiddlewareReleaseKind.Forms10g]  = [MiddlewareReleaseKind.Forms11g, MiddlewareReleaseKind.Forms12c, MiddlewareReleaseKind.Forms14c],
            [MiddlewareReleaseKind.Forms11g]  = [MiddlewareReleaseKind.Forms12c, MiddlewareReleaseKind.Forms14c],
            [MiddlewareReleaseKind.Forms12c]  = [MiddlewareReleaseKind.Forms14c],
            [MiddlewareReleaseKind.Forms14c]  = [],
        };

    public static IReadOnlyList<MiddlewareReleaseKind> GetSupportedSources()
        => [MiddlewareReleaseKind.Forms6i, MiddlewareReleaseKind.Forms10g, MiddlewareReleaseKind.Forms11g, MiddlewareReleaseKind.Forms12c];

    public static IReadOnlyList<MiddlewareReleaseKind> GetAllowedTargets(MiddlewareReleaseKind source)
    {
        if (source == MiddlewareReleaseKind.Unknown)
            return [];

        return AllowedTargets.TryGetValue(source, out var targets)
            ? targets
            : [];
    }

    public static bool IsValidUpgradePath(MiddlewareReleaseKind source, MiddlewareReleaseKind target)
        => GetAllowedTargets(source).Contains(target);

    public static string GetDisplayName(MiddlewareReleaseKind release) => release switch
    {
        MiddlewareReleaseKind.Forms6i  => "Oracle Forms 6i",
        MiddlewareReleaseKind.Forms10g => "Oracle Forms 10g",
        MiddlewareReleaseKind.Forms11g => "Oracle Forms 11g",
        MiddlewareReleaseKind.Forms12c => "Oracle Forms 12c",
        MiddlewareReleaseKind.Forms14c => "Oracle Forms 14c",
        _                              => "Unknown",
    };

    public static string GetShortLabel(MiddlewareReleaseKind release) => release switch
    {
        MiddlewareReleaseKind.Forms6i  => "6i",
        MiddlewareReleaseKind.Forms10g => "10g",
        MiddlewareReleaseKind.Forms11g => "11g",
        MiddlewareReleaseKind.Forms12c => "12c",
        MiddlewareReleaseKind.Forms14c => "14c",
        _                              => "?",
    };

    public static string DescribeUpgradePath(MiddlewareReleaseKind source, MiddlewareReleaseKind target)
    {
        if (!IsValidUpgradePath(source, target))
            return "No supported upgrade path.";

        return $"{GetDisplayName(source)} → {GetDisplayName(target)}";
    }
}
