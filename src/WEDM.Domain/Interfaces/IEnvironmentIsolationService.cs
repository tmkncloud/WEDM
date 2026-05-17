using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>
/// Manages deterministic environment isolation for every Oracle tool WEDM launches.
///
/// Guarantees:
///   • Every OUI / WLST / OPatch / Forms / OHS invocation executes inside an explicitly
///     controlled, sanitized environment — never inheriting ambient machine state.
///   • Stale ORACLE_HOME, WL_HOME, MW_HOME, CLASSPATH, WLST_HOME, OPatch residuals,
///     and JVM override variables are cleared before each tool runs.
///   • TEMP / TMP is scoped to the current deployment session, preventing cross-session
///     contamination from OUI JAR extraction residue.
///   • PATH is reconstructed from scratch: Windows system directories + JavaHome\bin
///     + tool-specific additions. Stale Oracle entries are excluded.
///   • All mutations are process-scoped (PowerShell $env: injection) — machine-level
///     PATH and registry are never modified by this service.
///   • Pre/post deployment snapshots enable drift detection after each phase.
///
/// The primary integration pattern is <see cref="BuildIsolatedEnvironment"/> →
/// use <see cref="IsolatedEnvironmentVariables.PowerShellPreamble"/> as the first
/// block in every PowerShell script body that launches an Oracle tool.
/// </summary>
public interface IEnvironmentIsolationService
{
    // ── Snapshot capture ──────────────────────────────────────────────────────

    /// <summary>
    /// Captures a point-in-time snapshot of all Oracle-relevant environment variables
    /// from the current process environment (process + machine scope on Windows).
    ///
    /// Call at three lifecycle points:
    ///   <list type="bullet">
    ///     <item>Before any deployment step runs — <see cref="SnapshotKind.PreDeployment"/></item>
    ///     <item>After all forward steps complete — <see cref="SnapshotKind.PostDeployment"/></item>
    ///     <item>After rollback completes — <see cref="SnapshotKind.PostRollback"/></item>
    ///   </list>
    ///
    /// Never throws — any read error is silently recorded in the snapshot.
    /// </summary>
    /// <param name="kind">The lifecycle point at which this snapshot is taken.</param>
    /// <param name="sessionId">Session ID to embed in the snapshot for correlation.</param>
    EnvironmentSnapshot CaptureSnapshot(SnapshotKind kind, Guid sessionId);

    // ── Context construction ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="DeploymentEnvironmentContext"/> for the given deployment session.
    ///
    /// The context is the authoritative source of truth for all environment variables
    /// injected into Oracle tool subprocesses.  It is constructed once at session start
    /// and mutated only to record path discoveries (e.g. after the OUI step resolves
    /// <see cref="DeploymentEnvironmentContext.MiddlewareHome"/>).
    ///
    /// Steps performed:
    ///   <list type="number">
    ///     <item>Resolve JavaHome, MiddlewareHome, OracleHome from config paths.</item>
    ///     <item>Create session-scoped TEMP root under config.Paths.TempDirectory.</item>
    ///     <item>Build sanitized PATH (system dirs + JavaHome\bin).</item>
    ///     <item>Apply isolation flags from the deployment config.</item>
    ///   </list>
    /// </summary>
    /// <param name="config">The deployment configuration for this session.</param>
    /// <param name="preDeploymentSnapshot">
    ///   The snapshot captured before any deployment action ran.
    ///   Used to initialise the context's baseline knowledge.
    /// </param>
    DeploymentEnvironmentContext BuildContext(
        DeploymentConfiguration config,
        EnvironmentSnapshot preDeploymentSnapshot);

    // ── Per-tool environment construction ─────────────────────────────────────

    /// <summary>
    /// Produces a fully isolated, ready-to-inject environment variable set for the
    /// specified Oracle tool.
    ///
    /// The returned <see cref="IsolatedEnvironmentVariables.PowerShellPreamble"/> is the
    /// primary output: prepend it verbatim to any PowerShell script body before the
    /// tool command.  It contains:
    ///   <list type="bullet">
    ///     <item><c>Remove-Item Env:VAR -ErrorAction SilentlyContinue</c> for every variable in <see cref="IsolatedEnvironmentVariables.ClearVariables"/></item>
    ///     <item><c>$env:VAR = 'value'</c> for every variable in <see cref="IsolatedEnvironmentVariables.SetVariables"/></item>
    ///   </list>
    ///
    /// Tool-specific variable sets:
    ///   <list type="bullet">
    ///     <item><see cref="OracleTool.OUI"/>  — sets JAVA_HOME, TEMP, TMP, PATH; clears MW/WL/ORACLE vars</item>
    ///     <item><see cref="OracleTool.WLST"/> — sets ORACLE_HOME, JAVA_HOME, TEMP, PATH; clears WLST residuals</item>
    ///     <item><see cref="OracleTool.OPatch"/> — sets ORACLE_HOME, JAVA_HOME, PATH (with OPatch dir); clears OPATCH_DEBUG</item>
    ///     <item><see cref="OracleTool.NodeManager"/> — sets JAVA_HOME, WL_HOME, TEMP; clears CLASSPATH</item>
    ///     <item><see cref="OracleTool.Forms"/>  — sets ORACLE_HOME, JAVA_HOME, PATH; clears Forms residuals</item>
    ///     <item><see cref="OracleTool.OHS"/>    — sets ORACLE_HOME, JAVA_HOME, PATH; clears OHS residuals</item>
    ///     <item>All tools: clear CLASSPATH, stale Oracle vars, JVM override vars</item>
    ///   </list>
    /// </summary>
    /// <param name="tool">Which Oracle tool is being launched.</param>
    /// <param name="context">The session environment context.</param>
    IsolatedEnvironmentVariables BuildIsolatedEnvironment(
        OracleTool tool,
        DeploymentEnvironmentContext context);

    // ── Pre-launch validation ─────────────────────────────────────────────────

    /// <summary>
    /// Validates that the environment is safe and correct for launching the specified
    /// Oracle tool.  Call this immediately before handing off to the tool.
    ///
    /// Checks performed:
    ///   <list type="bullet">
    ///     <item>JavaHome is set, non-empty, and java.exe exists at the expected path.</item>
    ///     <item>Tool-specific home (MiddlewareHome / OracleHome) is set and is a directory.</item>
    ///     <item>PATH does not contain stale Oracle or JDK entries that could shadow the intended runtime.</item>
    ///     <item>TEMP/TMP is the session-scoped directory (not %USERPROFILE% or system temp).</item>
    ///     <item>CLASSPATH is absent (to prevent classpath-based class loading surprises).</item>
    ///     <item>Tool-specific invariants (e.g. for OPatch: OPatch directory exists under OracleHome).</item>
    ///   </list>
    ///
    /// Returns <see cref="EnvironmentValidationResult.IsValid"/> = false when any blocker is found.
    /// Warnings are non-fatal; blockers prevent the tool from being launched.
    /// </summary>
    /// <param name="tool">The tool about to be launched.</param>
    /// <param name="env">The isolated environment variables that will be injected.</param>
    /// <param name="context">The session environment context.</param>
    EnvironmentValidationResult ValidateBeforeLaunch(
        OracleTool tool,
        IsolatedEnvironmentVariables env,
        DeploymentEnvironmentContext context);

    // ── Drift detection ───────────────────────────────────────────────────────

    /// <summary>
    /// Compares <paramref name="baseline"/> to <paramref name="current"/> and returns
    /// a structured report of all detected environment mutations.
    ///
    /// Mutations are classified as <em>expected</em> (e.g. WEDM itself set JAVA_HOME) or
    /// <em>unexpected</em> (e.g. an Oracle installer modified PATH or left TNS_ADMIN behind).
    ///
    /// The list of expected variable names is derived from <paramref name="context"/>:
    /// any variable that appears in the session's <see cref="IsolatedEnvironmentVariables.SetVariables"/>
    /// or <see cref="IsolatedEnvironmentVariables.ClearVariables"/> is treated as expected.
    ///
    /// Oracle-installer-introduced additions to PATH are always flagged as unexpected drift
    /// regardless of whether they match expected patterns.
    /// </summary>
    /// <param name="baseline">The earlier snapshot (typically <see cref="SnapshotKind.PreDeployment"/>).</param>
    /// <param name="current">The later snapshot (typically <see cref="SnapshotKind.PostDeployment"/>).</param>
    /// <param name="context">Used to classify mutations as expected or unexpected.</param>
    EnvironmentDriftReport DetectDrift(
        EnvironmentSnapshot baseline,
        EnvironmentSnapshot current,
        DeploymentEnvironmentContext context);

    // ── Scoped variable application ───────────────────────────────────────────

    /// <summary>
    /// Applies the variables in <paramref name="env"/> to the current process environment
    /// for the duration of the deployment step.
    ///
    /// Scope rules:
    ///   <list type="bullet">
    ///     <item><strong>Process-scoped (preferred)</strong> — written to <c>Environment.SetEnvironmentVariable(name, value, Process)</c>.
    ///           Automatically cleaned up when the engine process exits.</item>
    ///     <item><strong>Session-scoped (rare)</strong> — written via PowerShell preamble injection only.
    ///           Never persisted to machine or user scope.</item>
    ///   </list>
    ///
    /// The returned <see cref="IDisposable"/> restores all variables to their pre-call values
    /// when disposed.  Always wrap usage in a <c>using</c> block.
    ///
    /// <para>IMPORTANT: This method only applies variables to the <em>current process</em>.
    /// It does NOT affect subprocesses launched from PowerShell — those require the
    /// <see cref="IsolatedEnvironmentVariables.PowerShellPreamble"/> injection pattern.</para>
    /// </summary>
    /// <param name="env">The isolated environment variables to apply.</param>
    /// <returns>
    ///   A disposable scope that reverts all applied variables on disposal.
    /// </returns>
    IDisposable ApplyScopedVariables(IsolatedEnvironmentVariables env);

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>
    /// Produces a structured diagnostics report for the completed deployment session.
    ///
    /// The report includes:
    ///   <list type="bullet">
    ///     <item>Count of Oracle tool invocations that had isolation preamble applied.</item>
    ///     <item>Full list of variables cleared and injected during the session.</item>
    ///     <item>Environment drift report (pre vs post-deployment snapshot comparison).</item>
    ///     <item>Any validation warnings raised before tool launches.</item>
    ///     <item>PATH analysis: segment count, Oracle/Java segments, duplicates, stale entries.</item>
    ///   </list>
    ///
    /// Call after the last deployment step completes (or after rollback) to capture
    /// the full session diagnostic picture.
    /// </summary>
    /// <param name="context">The session environment context.</param>
    /// <param name="postDeploymentSnapshot">
    ///   Snapshot taken after all steps finished (or after rollback).
    ///   When null, drift detection is skipped.
    /// </param>
    EnvironmentIsolationReport GenerateDiagnostics(
        DeploymentEnvironmentContext context,
        EnvironmentSnapshot? postDeploymentSnapshot);

    // ── PATH analysis ─────────────────────────────────────────────────────────

    /// <summary>
    /// Analyses a raw PATH string and classifies each segment.
    ///
    /// Returns segment-level detail including: Oracle segments, Java segments,
    /// system segments, duplicates, stale entries, and any required entries that
    /// are missing from the PATH.
    ///
    /// Used for diagnostics and for validating the sanitized PATH before injection.
    /// </summary>
    /// <param name="rawPath">The PATH string to analyse (semicolon-separated on Windows).</param>
    /// <param name="context">Session context; used to determine which paths are "expected".</param>
    PathAnalysisResult AnalysePath(string rawPath, DeploymentEnvironmentContext context);
}
