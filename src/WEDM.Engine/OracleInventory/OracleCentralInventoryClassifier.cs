using WEDM.Domain.Models;

namespace WEDM.Engine.OracleInventory;

/// <summary>
/// Classifies central Oracle inventory XML into explicit lifecycle states.
/// Empty HOME_LIST is a valid clean-install state and must not be treated as corruption.
/// </summary>
public static class OracleCentralInventoryClassifier
{
    public const string EmptyInventoryMessage =
        "Central inventory is initialized but currently empty (clean-install state).";

    public static bool IsInstallBlocking(OracleCentralInventoryState state) =>
        state is OracleCentralInventoryState.Missing
            or OracleCentralInventoryState.Corrupted
            or OracleCentralInventoryState.BootstrapFailed;

    public static bool IsXmlReadable(OracleCentralInventoryState state) =>
        state is OracleCentralInventoryState.Empty
            or OracleCentralInventoryState.Healthy
            or OracleCentralInventoryState.Stale
            or OracleCentralInventoryState.Partial;

    public static bool IsBootstrapEligible(OracleCentralInventoryState state) =>
        state is OracleCentralInventoryState.BootstrapRequired;

    public static OracleCentralInventoryState RefineFromHomes(
        OracleCentralInventoryState baseState,
        IReadOnlyList<OracleInventoryHomeRecord> homes)
    {
        if (baseState is not (OracleCentralInventoryState.Healthy or OracleCentralInventoryState.Empty))
            return baseState;

        if (homes.Count == 0)
            return OracleCentralInventoryState.Empty;

        var staleCount = homes.Count(h => h.IsStale);
        if (staleCount == 0)
            return OracleCentralInventoryState.Healthy;

        return staleCount == homes.Count
            ? OracleCentralInventoryState.Stale
            : OracleCentralInventoryState.Partial;
    }
}
