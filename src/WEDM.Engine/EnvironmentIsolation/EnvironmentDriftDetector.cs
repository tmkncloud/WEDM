using WEDM.Domain.Models;

namespace WEDM.Engine.EnvironmentIsolation;

/// <summary>
/// Compares two <see cref="EnvironmentSnapshot"/> instances and produces a
/// structured <see cref="EnvironmentDriftReport"/> describing every detected mutation.
///
/// Mutation classification:
///   • <see cref="DriftKind.Added"/>        — variable appeared in current that was absent in baseline.
///   • <see cref="DriftKind.Removed"/>      — variable present in baseline is absent in current.
///   • <see cref="DriftKind.Changed"/>      — variable present in both, value changed.
///   • <see cref="DriftKind.PathAdded"/>    — PATH segment appeared in current that was absent in baseline.
///   • <see cref="DriftKind.PathRemoved"/>  — PATH segment present in baseline is absent in current.
///   • <see cref="DriftKind.PathReordered"/>— PATH segments are the same set but in a different order.
///
/// Each finding is tagged <see cref="EnvironmentDriftFinding.IsExpected"/>:
///   • <c>true</c>  — WEDM itself performed this mutation (it appears in <paramref name="expectedMutations"/>).
///   • <c>false</c> — external mutation; operator should review.
/// </summary>
public static class EnvironmentDriftDetector
{
    // ─────────────────────────────────────────────────────────────────────────
    // Public entry point
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares <paramref name="baseline"/> to <paramref name="current"/> and returns
    /// a structured drift report.
    /// </summary>
    /// <param name="baseline">Earlier snapshot (e.g. pre-deployment).</param>
    /// <param name="current">Later snapshot (e.g. post-deployment or post-rollback).</param>
    /// <param name="expectedMutations">
    ///   Variable names that WEDM explicitly mutated during this session.
    ///   Findings for these variables are tagged <see cref="EnvironmentDriftFinding.IsExpected"/> = true.
    ///   Pass <c>null</c> to treat all mutations as unexpected.
    /// </param>
    public static EnvironmentDriftReport Detect(
        EnvironmentSnapshot baseline,
        EnvironmentSnapshot current,
        IReadOnlySet<string>? expectedMutations = null)
    {
        var expected = expectedMutations
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var findings = new List<EnvironmentDriftFinding>();

        // ── Scalar variable drift ─────────────────────────────────────────────
        CompareScalar(findings, "ORACLE_HOME",     baseline.OracleHome,    current.OracleHome,    expected);
        CompareScalar(findings, "WL_HOME",         baseline.WlHome,        current.WlHome,        expected);
        CompareScalar(findings, "MW_HOME",         baseline.MwHome,        current.MwHome,        expected);
        CompareScalar(findings, "WLST_HOME",       baseline.WlstHome,      current.WlstHome,      expected);
        CompareScalar(findings, "WLST_PROPERTIES", baseline.WlstProperties, current.WlstProperties, expected);
        CompareScalar(findings, "JAVA_HOME",       baseline.JavaHome,      current.JavaHome,      expected);
        CompareScalar(findings, "JAVA_OPTS",       baseline.JavaOpts,      current.JavaOpts,      expected);
        CompareScalar(findings, "JVM_ARGS",        baseline.JvmArgs,       current.JvmArgs,       expected);
        CompareScalar(findings, "CLASSPATH",       baseline.Classpath,     current.Classpath,     expected);
        CompareScalar(findings, "TEMP",            baseline.Temp,          current.Temp,          expected);
        CompareScalar(findings, "TMP",             baseline.Tmp,           current.Tmp,           expected);
        CompareScalar(findings, "OPATCH_DEBUG",    baseline.OpatchDebug,   current.OpatchDebug,   expected);
        CompareScalar(findings, "ORACLE_SID",      baseline.OracleSid,     current.OracleSid,     expected);
        CompareScalar(findings, "TNS_ADMIN",       baseline.TnsAdmin,      current.TnsAdmin,      expected);

        // ── PATH drift ────────────────────────────────────────────────────────
        ComparePathSegments(findings, baseline.Path, current.Path, expected);

        return new EnvironmentDriftReport
        {
            Baseline    = baseline.Kind,
            Current     = current.Kind,
            Findings    = findings,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scalar comparison helper
    // ─────────────────────────────────────────────────────────────────────────

    private static void CompareScalar(
        List<EnvironmentDriftFinding> findings,
        string variableName,
        string? baselineValue,
        string? currentValue,
        IReadOnlySet<string> expected)
    {
        var baselineEmpty = string.IsNullOrWhiteSpace(baselineValue);
        var currentEmpty  = string.IsNullOrWhiteSpace(currentValue);

        if (baselineEmpty && currentEmpty) return; // no change

        DriftKind kind;
        string description;

        if (baselineEmpty && !currentEmpty)
        {
            kind        = DriftKind.Added;
            description = $"{variableName} appeared: '{currentValue}'";
        }
        else if (!baselineEmpty && currentEmpty)
        {
            kind        = DriftKind.Removed;
            description = $"{variableName} removed (was: '{baselineValue}')";
        }
        else if (!string.Equals(baselineValue, currentValue, StringComparison.OrdinalIgnoreCase))
        {
            kind        = DriftKind.Changed;
            description = $"{variableName} changed: '{baselineValue}' → '{currentValue}'";
        }
        else
        {
            return; // same value (case-insensitive)
        }

        findings.Add(new EnvironmentDriftFinding
        {
            VariableName   = variableName,
            Kind           = kind,
            BaselineValue  = baselineValue,
            CurrentValue   = currentValue,
            IsExpected     = expected.Contains(variableName),
            Description    = description,
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH segment comparison
    // ─────────────────────────────────────────────────────────────────────────

    private static void ComparePathSegments(
        List<EnvironmentDriftFinding> findings,
        string? baselinePath,
        string? currentPath,
        IReadOnlySet<string> expected)
    {
        if (baselinePath == currentPath) return; // fast path: identical strings

        var baselineSegs = ParsePath(baselinePath);
        var currentSegs  = ParsePath(currentPath);

        var baselineSet  = new HashSet<string>(baselineSegs, StringComparer.OrdinalIgnoreCase);
        var currentSet   = new HashSet<string>(currentSegs,  StringComparer.OrdinalIgnoreCase);

        // Segments added in current that were not in baseline
        foreach (var seg in currentSet.Except(baselineSet, StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(new EnvironmentDriftFinding
            {
                VariableName  = "PATH",
                Kind          = DriftKind.PathAdded,
                BaselineValue = null,
                CurrentValue  = seg,
                IsExpected    = expected.Contains("PATH"),
                Description   = $"PATH segment added: '{seg}'",
            });
        }

        // Segments removed from baseline that are not in current
        foreach (var seg in baselineSet.Except(currentSet, StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(new EnvironmentDriftFinding
            {
                VariableName  = "PATH",
                Kind          = DriftKind.PathRemoved,
                BaselineValue = seg,
                CurrentValue  = null,
                IsExpected    = expected.Contains("PATH"),
                Description   = $"PATH segment removed: '{seg}'",
            });
        }

        // Same segments, different order — detect reorder if segments are identical but order changed
        if (!findings.Any(f => f.VariableName == "PATH") && baselineSegs.Count == currentSegs.Count)
        {
            bool reordered = !baselineSegs.SequenceEqual(currentSegs, StringComparer.OrdinalIgnoreCase);
            if (reordered)
            {
                findings.Add(new EnvironmentDriftFinding
                {
                    VariableName  = "PATH",
                    Kind          = DriftKind.PathReordered,
                    BaselineValue = baselinePath,
                    CurrentValue  = currentPath,
                    IsExpected    = expected.Contains("PATH"),
                    Description   = "PATH segment order changed (same set, different sequence)",
                });
            }
        }
    }

    private static List<string> ParsePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        return path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Convenience: build the expected mutation set from an IsolatedEnvironmentVariables
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Constructs the set of variable names that were intentionally mutated by WEDM
    /// for a given <see cref="IsolatedEnvironmentVariables"/> instance.
    /// Pass this as <c>expectedMutations</c> to <see cref="Detect"/>.
    /// </summary>
    public static IReadOnlySet<string> BuildExpectedMutationSet(
        IEnumerable<IsolatedEnvironmentVariables> allInjections)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var env in allInjections)
        {
            foreach (var key in env.SetVariables.Keys)
                set.Add(key);
            foreach (var key in env.ClearVariables)
                set.Add(key);
        }
        return set;
    }
}
