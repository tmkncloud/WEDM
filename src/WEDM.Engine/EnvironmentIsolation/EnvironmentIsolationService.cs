using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.EnvironmentIsolation;

/// <summary>
/// Main orchestrator for environment isolation.
/// Implements <see cref="IEnvironmentIsolationService"/>.
///
/// Thread-safety: this service is stateless — all per-session state lives in
/// <see cref="DeploymentEnvironmentContext"/> which is owned by the caller.
/// Multiple sessions can call this service concurrently without interference.
/// </summary>
public sealed class EnvironmentIsolationService : IEnvironmentIsolationService
{
    private readonly ILoggingService _log;
    private readonly ProcessEnvironmentBuilder _builder = new();

    // Tracks all injections performed during a session for drift classification
    // Key: SessionId.ToString(), Value: accumulated IsolatedEnvironmentVariables list
    private readonly Dictionary<Guid, List<IsolatedEnvironmentVariables>> _sessionInjections =
        new();

    private readonly object _lock = new();

    public EnvironmentIsolationService(ILoggingService log)
    {
        _log = log;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Snapshot capture
    // ─────────────────────────────────────────────────────────────────────────

    public EnvironmentSnapshot CaptureSnapshot(SnapshotKind kind, Guid sessionId)
    {
        try
        {
            var path     = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var segments = PathSanitizer.Split(path).ToList().AsReadOnly();

            return new EnvironmentSnapshot
            {
                Kind            = kind,
                SessionId       = sessionId,
                CapturedAt      = DateTimeOffset.UtcNow,
                OracleHome      = ReadVar("ORACLE_HOME"),
                WlHome          = ReadVar("WL_HOME"),
                MwHome          = ReadVar("MW_HOME"),
                WlstHome        = ReadVar("WLST_HOME"),
                WlstProperties  = ReadVar("WLST_PROPERTIES"),
                JavaHome        = ReadVar("JAVA_HOME"),
                JavaOpts        = ReadVar("JAVA_OPTS"),
                JvmArgs         = ReadVar("JVM_ARGS"),
                Classpath       = ReadVar("CLASSPATH"),
                Path            = path,
                PathSegments    = segments,
                Temp            = ReadVar("TEMP"),
                Tmp             = ReadVar("TMP"),
                UserProfile     = ReadVar("USERPROFILE"),
                AppData         = ReadVar("APPDATA"),
                ProgramData     = ReadVar("PROGRAMDATA"),
                SystemRoot      = ReadVar("SystemRoot"),
                WorkingDirectory = Environment.CurrentDirectory,
                OpatchDebug     = ReadVar("OPATCH_DEBUG"),
                OracleSid       = ReadVar("ORACLE_SID"),
                TnsAdmin        = ReadVar("TNS_ADMIN"),
            };
        }
        catch (Exception ex)
        {
            _log.Warning($"[EnvIsolation] CaptureSnapshot({kind}) failed: {ex.Message}", "EnvironmentIsolation");
            return new EnvironmentSnapshot { Kind = kind, SessionId = sessionId, CapturedAt = DateTimeOffset.UtcNow };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Context construction
    // ─────────────────────────────────────────────────────────────────────────

    public DeploymentEnvironmentContext BuildContext(
        DeploymentConfiguration config,
        EnvironmentSnapshot preDeploymentSnapshot)
    {
        var sessionId = Guid.NewGuid();

        // Session-scoped temp directory: prevents cross-session OUI JAR extraction residue
        var tempRoot = Path.Combine(
            config.Paths.TempDirectory ?? Path.GetTempPath(),
            $"wedm-session-{sessionId:N}"[..22]);  // 22 chars: "wedm-session-" + 8 hex chars

        var javaHome        = ResolveJavaHome(config);
        var middlewareHome  = config.Paths.MiddlewareHome ?? string.Empty;
        var oracleHome      = config.Paths.MiddlewareHome ?? string.Empty;  // WLS 12c: Oracle home = MW home
        var inventoryLocation = config.Paths.OracleInventory ?? string.Empty;

        var sanitizedPath = PathSanitizer.Build(
            GetMachinePath(),
            prependPaths: string.IsNullOrWhiteSpace(javaHome) ? null : [$@"{javaHome}\bin"],
            includeNonOracle: true);

        var ctx = new DeploymentEnvironmentContext
        {
            SessionId           = sessionId,
            MiddlewareHome      = middlewareHome,
            OracleHome          = oracleHome,
            JavaHome            = javaHome,
            InventoryLocation   = inventoryLocation,
            TempRoot            = tempRoot,
            WorkingDirectory    = tempRoot,
            SanitizedPath       = sanitizedPath,
            PreDeploymentSnapshot = preDeploymentSnapshot,
            // Isolation flags — always enabled; these are intentional by design
            ClearClasspath      = true,
            ClearStaleOracleVars = true,
            ClearWlstResiduals  = true,
            ClearJvmOverrideVars = true,
            ClearOpatchResiduals = true,
        };

        _log.Info(
            $"[EnvIsolation] Context built — session={sessionId:N}, " +
            $"JavaHome={javaHome}, MW={middlewareHome}, TempRoot={tempRoot}",
            "EnvironmentIsolation");

        return ctx;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-tool environment construction
    // ─────────────────────────────────────────────────────────────────────────

    public IsolatedEnvironmentVariables BuildIsolatedEnvironment(
        OracleTool tool,
        DeploymentEnvironmentContext context)
    {
        var env = _builder.Build(tool, context);

        // Record the injection for later drift classification
        lock (_lock)
        {
            if (!_sessionInjections.TryGetValue(context.SessionId, out var list))
            {
                list = new List<IsolatedEnvironmentVariables>();
                _sessionInjections[context.SessionId] = list;
            }
            list.Add(env);
        }

        _log.Info(
            $"[EnvIsolation] Isolated env built — {env.DiagnosticSummary}",
            "EnvironmentIsolation");

        return env;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pre-launch validation
    // ─────────────────────────────────────────────────────────────────────────

    public EnvironmentValidationResult ValidateBeforeLaunch(
        OracleTool tool,
        IsolatedEnvironmentVariables env,
        DeploymentEnvironmentContext context)
    {
        var findings = new List<string>();
        var warnings = new List<string>();
        var blockers = new List<string>();

        // 1. JavaHome validation (all tools except JdkInstaller)
        if (tool != OracleTool.JdkInstaller)
        {
            if (string.IsNullOrWhiteSpace(context.JavaHome))
            {
                blockers.Add("JAVA_HOME is not set in the deployment context.");
            }
            else
            {
                var javaExe = Path.Combine(context.JavaHome, "bin", "java.exe");
                if (!File.Exists(javaExe))
                    blockers.Add($"java.exe not found at expected path: {javaExe}");
                else
                    findings.Add($"java.exe located: {javaExe}");
            }
        }

        // 2. Tool-specific home validation
        switch (tool)
        {
            case OracleTool.WLST:
            case OracleTool.OPatch:
            case OracleTool.Forms:
            case OracleTool.OHS:
            case OracleTool.RCU:
                if (string.IsNullOrWhiteSpace(context.OracleHome))
                    blockers.Add($"ORACLE_HOME is not set in the deployment context (required for {tool}).");
                else if (!Directory.Exists(context.OracleHome))
                    blockers.Add($"ORACLE_HOME directory does not exist: {context.OracleHome}");
                else
                    findings.Add($"ORACLE_HOME validated: {context.OracleHome}");
                break;

            case OracleTool.NodeManager:
                if (string.IsNullOrWhiteSpace(context.MiddlewareHome))
                    blockers.Add("MIDDLEWARE_HOME is not set (required for NodeManager).");
                break;
        }

        // 3. OPatch-specific: opatch.bat must exist
        if (tool == OracleTool.OPatch && !string.IsNullOrWhiteSpace(context.OracleHome))
        {
            var opatchBat = Path.Combine(context.OracleHome, "OPatch", "opatch.bat");
            var opatchBatAlt = Path.Combine(context.OracleHome, "oracle_common", "OPatch", "opatch.bat");
            if (!File.Exists(opatchBat) && !File.Exists(opatchBatAlt))
                warnings.Add($"opatch.bat not found at {opatchBat} or {opatchBatAlt}; OPatch may fail.");
            else
                findings.Add($"opatch.bat located.");
        }

        // 4. TEMP isolation check
        if (!string.IsNullOrWhiteSpace(context.TempRoot))
        {
            if (env.SetVariables.TryGetValue("TEMP", out var injectedTemp))
            {
                if (!string.Equals(injectedTemp, context.TempRoot, StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"Injected TEMP ({injectedTemp}) does not match context TempRoot ({context.TempRoot}).");
                else
                    findings.Add($"TEMP correctly scoped to session: {injectedTemp}");
            }
            else
            {
                warnings.Add("Session-scoped TEMP is configured but not injected into this tool's environment.");
            }
        }

        // 5. PATH stale entry check
        if (env.SetVariables.TryGetValue("PATH", out var injectedPath))
        {
            var analysis = PathSanitizer.Analyse(injectedPath);
            if (analysis.StaleSegments.Count > 0)
                warnings.Add($"Sanitized PATH still contains {analysis.StaleSegments.Count} stale Oracle entries: " +
                             string.Join(", ", analysis.StaleSegments));
            else
                findings.Add($"PATH is clean: {analysis.AllSegments.Count} segment(s), no stale Oracle entries.");

            if (analysis.DuplicateSegments.Count > 0)
                warnings.Add($"PATH contains {analysis.DuplicateSegments.Count} duplicate segments.");
        }

        // 6. CLASSPATH clear check
        if (env.ClearVariables.Contains("CLASSPATH", StringComparer.OrdinalIgnoreCase))
            findings.Add("CLASSPATH will be cleared before tool launch.");
        else
            warnings.Add("CLASSPATH is not in the clear list; class-loading surprises may occur.");

        var isValid = blockers.Count == 0;
        if (!isValid)
            _log.Warning(
                $"[EnvIsolation] Pre-launch validation BLOCKED for {tool}: {string.Join("; ", blockers)}",
                "EnvironmentIsolation");
        else
            _log.Info(
                $"[EnvIsolation] Pre-launch validation passed for {tool} ({findings.Count} checks, {warnings.Count} warnings).",
                "EnvironmentIsolation");

        return new EnvironmentValidationResult
        {
            Tool     = tool,
            IsValid  = isValid,
            Findings = findings.AsReadOnly(),
            Warnings = warnings.AsReadOnly(),
            Blockers = blockers.AsReadOnly(),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Drift detection
    // ─────────────────────────────────────────────────────────────────────────

    public EnvironmentDriftReport DetectDrift(
        EnvironmentSnapshot baseline,
        EnvironmentSnapshot current,
        DeploymentEnvironmentContext context)
    {
        IReadOnlySet<string>? expectedMutations = null;

        lock (_lock)
        {
            if (_sessionInjections.TryGetValue(context.SessionId, out var list))
                expectedMutations = EnvironmentDriftDetector.BuildExpectedMutationSet(list);
        }

        var report = EnvironmentDriftDetector.Detect(baseline, current, expectedMutations);

        if (report.HasDrift)
        {
            var unexpected = report.UnexpectedFindings.Count;
            if (unexpected > 0)
                _log.Warning(
                    $"[EnvIsolation] Drift detected: {report.Summary}",
                    "EnvironmentIsolation");
            else
                _log.Info(
                    $"[EnvIsolation] Expected drift only: {report.Summary}",
                    "EnvironmentIsolation");
        }
        else
        {
            _log.Info("[EnvIsolation] No environment drift detected.", "EnvironmentIsolation");
        }

        return report;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scoped variable application
    // ─────────────────────────────────────────────────────────────────────────

    public IDisposable ApplyScopedVariables(IsolatedEnvironmentVariables env)
    {
        // Capture current values so we can restore them on dispose
        var restoreMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Variables to clear
        foreach (var name in env.ClearVariables)
        {
            restoreMap[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null); // removes the variable
        }

        // Variables to set
        foreach (var (name, value) in env.SetVariables)
        {
            if (!restoreMap.ContainsKey(name))
                restoreMap[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        _log.Info(
            $"[EnvIsolation] Applied scoped variables for {env.Tool}: " +
            $"set={env.SetVariables.Count}, cleared={env.ClearVariables.Count}",
            "EnvironmentIsolation");

        return new ScopedVariableRestorer(restoreMap, _log);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Diagnostics
    // ─────────────────────────────────────────────────────────────────────────

    public EnvironmentIsolationReport GenerateDiagnostics(
        DeploymentEnvironmentContext context,
        EnvironmentSnapshot? postDeploymentSnapshot)
    {
        List<IsolatedEnvironmentVariables> injections;
        lock (_lock)
        {
            _sessionInjections.TryGetValue(context.SessionId, out var list);
            injections = list ?? [];
        }

        var report = new EnvironmentIsolationReport
        {
            SessionId              = context.SessionId,
            GeneratedAt            = DateTimeOffset.UtcNow,
            PreambleInjectionCount = injections.Count,
        };

        // Summarise tool invocations
        foreach (var inj in injections)
            report.IsolatedToolInvocations.Add($"{inj.Tool} @ {inj.BuiltAt:HH:mm:ss}");

        // Aggregate cleared + injected variable names
        var clearedSet  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var injectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inj in injections)
        {
            foreach (var v in inj.ClearVariables)
                clearedSet.Add(v);
            foreach (var k in inj.SetVariables.Keys)
                injectedSet.Add(k);
        }

        report.ClearedVariables.AddRange(clearedSet.Order());
        report.InjectedVariables.AddRange(injectedSet.Order());

        // Drift detection
        if (context.PreDeploymentSnapshot is not null && postDeploymentSnapshot is not null)
        {
            report.DriftReport = DetectDrift(
                context.PreDeploymentSnapshot,
                postDeploymentSnapshot,
                context);
        }

        // Validation warnings (PATH analysis on the session sanitized path)
        if (!string.IsNullOrWhiteSpace(context.SanitizedPath))
        {
            var pathAnalysis = PathSanitizer.Analyse(context.SanitizedPath);
            if (pathAnalysis.HasStaleEntries)
                report.ValidationWarnings.Add(
                    $"Session sanitized PATH still contains stale Oracle entries: " +
                    string.Join(", ", pathAnalysis.StaleSegments));
            if (pathAnalysis.HasDuplicates)
                report.ValidationWarnings.Add(
                    $"Session sanitized PATH has {pathAnalysis.DuplicateSegments.Count} duplicate segment(s).");
        }

        _log.Info(
            $"[EnvIsolation] Diagnostics generated: {injections.Count} injection(s), " +
            $"drift={report.DriftDetected}, warnings={report.ValidationWarnings.Count}",
            "EnvironmentIsolation");

        return report;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH analysis
    // ─────────────────────────────────────────────────────────────────────────

    public PathAnalysisResult AnalysePath(string rawPath, DeploymentEnvironmentContext context)
    {
        var required = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.JavaHome))
            required.Add($@"{context.JavaHome}\bin");

        return PathSanitizer.Analyse(rawPath, required);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string? ReadVar(string name)
        => Environment.GetEnvironmentVariable(name);

    private static string GetMachinePath()
        => Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

    private static string ResolveJavaHome(DeploymentConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.Java.JavaHome))
            return config.Java.JavaHome;
        return Environment.GetEnvironmentVariable("JAVA_HOME") ?? string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inner type: scoped variable restorer
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class ScopedVariableRestorer : IDisposable
    {
        private readonly Dictionary<string, string?> _restoreMap;
        private readonly ILoggingService _log;
        private bool _disposed;

        public ScopedVariableRestorer(Dictionary<string, string?> restoreMap, ILoggingService log)
        {
            _restoreMap = restoreMap;
            _log        = log;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var (name, previousValue) in _restoreMap)
            {
                try
                {
                    Environment.SetEnvironmentVariable(name, previousValue); // null = remove
                }
                catch (Exception ex)
                {
                    _log.Warning($"[EnvIsolation] Failed to restore {name}: {ex.Message}", "EnvironmentIsolation");
                }
            }

            _log.Info(
                $"[EnvIsolation] Scoped environment restored ({_restoreMap.Count} variable(s)).",
                "EnvironmentIsolation");
        }
    }
}
