using System.Runtime.InteropServices;
using System.Text;
using WEDM.Domain.Models;

namespace WEDM.Engine.EnvironmentIsolation;

/// <summary>
/// Builds per-tool isolated environment variable sets and their ready-to-inject
/// PowerShell preamble strings.
///
/// Each Oracle tool requires a slightly different set of environment variables.
/// This class encodes those requirements and produces:
///   • <see cref="IsolatedEnvironmentVariables.SetVariables"/>   — variables to inject
///   • <see cref="IsolatedEnvironmentVariables.ClearVariables"/> — variables to remove
///   • <see cref="IsolatedEnvironmentVariables.PowerShellPreamble"/> — ready-to-paste PS block
///
/// Integration pattern (all Oracle tool scripts):
/// <code>
///   var env      = builder.Build(OracleTool.WLST, context);
///   var psScript = env.PowerShellPreamble + Environment.NewLine + actualScriptBody;
/// </code>
/// </summary>
public sealed class ProcessEnvironmentBuilder
{
    // ─────────────────────────────────────────────────────────────────────────
    // Variables that must ALWAYS be cleared (universal across all Oracle tools)
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly string[] UniversalClearVars =
    [
        // Stale Oracle homes
        "ORACLE_HOME",
        "WL_HOME",
        "MW_HOME",
        "ORACLE_BASE",

        // CLASSPATH — causes class-loading surprises; each tool manages its own
        "CLASSPATH",

        // JVM tuning overrides — may cause OutOfMemory or heap-size mismatches
        "JAVA_OPTS",
        "_JAVA_OPTIONS",
        "JVM_ARGS",
        "JAVA_TOOL_OPTIONS",
    ];

    // Variables cleared only when the deployment config flag ClearWlstResiduals = true
    private static readonly string[] WlstResidualVars =
    [
        "WLST_HOME",
        "WLST_PROPERTIES",
    ];

    // Variables cleared only when ClearOpatchResiduals = true
    private static readonly string[] OpatchResidualVars =
    [
        "OPATCH_DEBUG",
        "OPATCH_PATCH_SELECTION_FILTER",
    ];

    // ─────────────────────────────────────────────────────────────────────────
    // Build
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="IsolatedEnvironmentVariables"/> for the specified Oracle tool.
    /// </summary>
    public IsolatedEnvironmentVariables Build(OracleTool tool, DeploymentEnvironmentContext ctx)
        => tool switch
        {
            OracleTool.OUI         => BuildForOui(ctx),
            OracleTool.WLST        => BuildForWlst(ctx),
            OracleTool.OPatch      => BuildForOpatch(ctx),
            OracleTool.NodeManager => BuildForNodeManager(ctx),
            OracleTool.Forms       => BuildForForms(ctx),
            OracleTool.OHS         => BuildForOhs(ctx),
            OracleTool.JdkInstaller => BuildForJdkInstaller(ctx),
            OracleTool.RCU         => BuildForRcu(ctx),
            _                      => BuildGeneric(ctx),
        };

    // ─────────────────────────────────────────────────────────────────────────
    // Per-tool profiles
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// OUI (Oracle Universal Installer) — java -jar installer.jar -silent
    ///
    /// OUI is a Java process; it must NOT see stale ORACLE_HOME, WL_HOME, or
    /// any prior Oracle entries in PATH.  It also must NOT inherit a stale TEMP
    /// directory (OUI extracts JARs and native code into TEMP).
    /// </summary>
    private IsolatedEnvironmentVariables BuildForOui(DeploymentEnvironmentContext ctx)
    {
        var clear = BuildClearList(ctx, clearWlst: true, clearOpatch: true);

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            set["JAVA_HOME"] = ctx.JavaHome;

        // Session-scoped TEMP/TMP to isolate OUI JAR extraction residue
        if (!string.IsNullOrWhiteSpace(ctx.TempRoot))
        {
            set["TEMP"] = ctx.TempRoot;
            set["TMP"]  = ctx.TempRoot;
        }

        // OUI itself manages ORACLE_HOME; do NOT set it — OUI writes it
        // Build PATH: Java only + system paths; no Oracle entries
        var path = PathSanitizer.Build(
            GetMachinePath(),
            prependPaths: string.IsNullOrWhiteSpace(ctx.JavaHome) ? null : [$@"{ctx.JavaHome}\bin"],
            includeNonOracle: true);
        set["PATH"] = path;

        return Assemble(OracleTool.OUI, set, clear, ctx);
    }

    /// <summary>
    /// WLST (WebLogic Scripting Tool) — wlst.cmd script.py
    ///
    /// WLST needs ORACLE_HOME and JAVA_HOME set explicitly; it must NOT inherit
    /// stale WLST_HOME or WLST_PROPERTIES from a prior session.
    /// </summary>
    private IsolatedEnvironmentVariables BuildForWlst(DeploymentEnvironmentContext ctx)
    {
        var clear = BuildClearList(ctx, clearWlst: ctx.ClearWlstResiduals, clearOpatch: false);

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ctx.OracleHome))
            set["ORACLE_HOME"] = ctx.OracleHome;
        if (!string.IsNullOrWhiteSpace(ctx.MiddlewareHome))
            set["MW_HOME"] = ctx.MiddlewareHome;
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            set["JAVA_HOME"] = ctx.JavaHome;
        if (!string.IsNullOrWhiteSpace(ctx.TempRoot))
        {
            set["TEMP"] = ctx.TempRoot;
            set["TMP"]  = ctx.TempRoot;
        }

        var wlstPrependPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            wlstPrependPaths.Add($@"{ctx.JavaHome}\bin");
        if (!string.IsNullOrWhiteSpace(ctx.OracleHome))
            wlstPrependPaths.Add($@"{ctx.OracleHome}\oracle_common\common\bin");

        var path = PathSanitizer.Build(GetMachinePath(), wlstPrependPaths, includeNonOracle: true);
        set["PATH"] = path;

        return Assemble(OracleTool.WLST, set, clear, ctx);
    }

    /// <summary>
    /// OPatch — opatch.bat napply / apply / lsinventory
    ///
    /// OPatch uses ORACLE_HOME to find its own libraries; it must also have the
    /// OPatch directory at the front of PATH so it can invoke its native binaries.
    /// OPATCH_DEBUG must be cleared to prevent verbose log spam that masks errors.
    /// </summary>
    private IsolatedEnvironmentVariables BuildForOpatch(DeploymentEnvironmentContext ctx)
    {
        var clear = BuildClearList(ctx, clearWlst: true, clearOpatch: ctx.ClearOpatchResiduals);

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ctx.OracleHome))
            set["ORACLE_HOME"] = ctx.OracleHome;
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            set["JAVA_HOME"] = ctx.JavaHome;
        if (!string.IsNullOrWhiteSpace(ctx.TempRoot))
        {
            set["TEMP"] = ctx.TempRoot;
            set["TMP"]  = ctx.TempRoot;
        }

        var prependPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            prependPaths.Add($@"{ctx.JavaHome}\bin");

        // OPatch directory under OracleHome (also oracle_common\OPatch as fallback for FMW)
        if (!string.IsNullOrWhiteSpace(ctx.OracleHome))
        {
            prependPaths.Add($@"{ctx.OracleHome}\OPatch");
            prependPaths.Add($@"{ctx.OracleHome}\oracle_common\OPatch");
        }

        var path = PathSanitizer.Build(GetMachinePath(), prependPaths, includeNonOracle: true);
        set["PATH"] = path;

        return Assemble(OracleTool.OPatch, set, clear, ctx);
    }

    /// <summary>
    /// NodeManager — the WLS process manager daemon.
    ///
    /// NodeManager needs JAVA_HOME and WL_HOME; it must NOT inherit CLASSPATH
    /// as WLS manages its own class loading from WL_HOME\server\lib.
    /// </summary>
    private IsolatedEnvironmentVariables BuildForNodeManager(DeploymentEnvironmentContext ctx)
    {
        var clear = BuildClearList(ctx, clearWlst: false, clearOpatch: true);

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            set["JAVA_HOME"] = ctx.JavaHome;
        if (!string.IsNullOrWhiteSpace(ctx.MiddlewareHome))
            set["MW_HOME"] = ctx.MiddlewareHome;
        // WL_HOME = MiddlewareHome\wlserver for WLS 12c
        if (!string.IsNullOrWhiteSpace(ctx.MiddlewareHome))
            set["WL_HOME"] = Path.Combine(ctx.MiddlewareHome, "wlserver");
        if (!string.IsNullOrWhiteSpace(ctx.TempRoot))
        {
            set["TEMP"] = ctx.TempRoot;
            set["TMP"]  = ctx.TempRoot;
        }

        var prependPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            prependPaths.Add($@"{ctx.JavaHome}\bin");

        var path = PathSanitizer.Build(GetMachinePath(), prependPaths, includeNonOracle: true);
        set["PATH"] = path;

        return Assemble(OracleTool.NodeManager, set, clear, ctx);
    }

    /// <summary>
    /// Oracle Forms / Reports installer or runtime.
    ///
    /// Forms needs ORACLE_HOME, JAVA_HOME, and a PATH that includes both the Forms
    /// bin directory and the java bin directory.
    /// </summary>
    private IsolatedEnvironmentVariables BuildForForms(DeploymentEnvironmentContext ctx)
    {
        var clear = BuildClearList(ctx, clearWlst: true, clearOpatch: true);

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ctx.OracleHome))
            set["ORACLE_HOME"] = ctx.OracleHome;
        if (!string.IsNullOrWhiteSpace(ctx.MiddlewareHome))
            set["MW_HOME"] = ctx.MiddlewareHome;
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            set["JAVA_HOME"] = ctx.JavaHome;
        if (!string.IsNullOrWhiteSpace(ctx.TempRoot))
        {
            set["TEMP"] = ctx.TempRoot;
            set["TMP"]  = ctx.TempRoot;
        }

        var prependPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            prependPaths.Add($@"{ctx.JavaHome}\bin");
        if (!string.IsNullOrWhiteSpace(ctx.OracleHome))
            prependPaths.Add($@"{ctx.OracleHome}\bin");

        var path = PathSanitizer.Build(GetMachinePath(), prependPaths, includeNonOracle: true);
        set["PATH"] = path;

        return Assemble(OracleTool.Forms, set, clear, ctx);
    }

    /// <summary>
    /// Oracle HTTP Server (OHS) — the web tier component.
    ///
    /// OHS needs ORACLE_HOME for its configuration files and a PATH that includes
    /// the OHS bin directory for native tools (apachectl, openssl wrappers, etc.).
    /// </summary>
    private IsolatedEnvironmentVariables BuildForOhs(DeploymentEnvironmentContext ctx)
    {
        var clear = BuildClearList(ctx, clearWlst: true, clearOpatch: true);

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ctx.OracleHome))
            set["ORACLE_HOME"] = ctx.OracleHome;
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            set["JAVA_HOME"] = ctx.JavaHome;
        if (!string.IsNullOrWhiteSpace(ctx.TempRoot))
        {
            set["TEMP"] = ctx.TempRoot;
            set["TMP"]  = ctx.TempRoot;
        }

        var prependPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            prependPaths.Add($@"{ctx.JavaHome}\bin");
        if (!string.IsNullOrWhiteSpace(ctx.OracleHome))
            prependPaths.Add($@"{ctx.OracleHome}\bin");

        var path = PathSanitizer.Build(GetMachinePath(), prependPaths, includeNonOracle: true);
        set["PATH"] = path;

        return Assemble(OracleTool.OHS, set, clear, ctx);
    }

    /// <summary>
    /// JDK Installer (MSI / EXE) — silent JDK installation.
    ///
    /// The installer itself does not need JAVA_HOME (it's installing Java).
    /// We do need a clean TEMP and minimal PATH.
    /// </summary>
    private IsolatedEnvironmentVariables BuildForJdkInstaller(DeploymentEnvironmentContext ctx)
    {
        var clear = BuildClearList(ctx, clearWlst: true, clearOpatch: true);

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ctx.TempRoot))
        {
            set["TEMP"] = ctx.TempRoot;
            set["TMP"]  = ctx.TempRoot;
        }

        // Minimal PATH — system paths only (JDK installer manages its own execution)
        var path = PathSanitizer.Build(GetMachinePath(), null, includeNonOracle: false);
        set["PATH"] = path;

        return Assemble(OracleTool.JdkInstaller, set, clear, ctx);
    }

    /// <summary>
    /// RCU (Repository Creation Utility) — database schema provisioning.
    ///
    /// RCU is a Java Swing app; needs JAVA_HOME and ORACLE_HOME.
    /// CLASSPATH must be cleared — RCU builds its own via rcu.bat.
    /// </summary>
    private IsolatedEnvironmentVariables BuildForRcu(DeploymentEnvironmentContext ctx)
    {
        var clear = BuildClearList(ctx, clearWlst: true, clearOpatch: true);

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ctx.OracleHome))
            set["ORACLE_HOME"] = ctx.OracleHome;
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            set["JAVA_HOME"] = ctx.JavaHome;
        if (!string.IsNullOrWhiteSpace(ctx.TempRoot))
        {
            set["TEMP"] = ctx.TempRoot;
            set["TMP"]  = ctx.TempRoot;
        }

        var prependPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            prependPaths.Add($@"{ctx.JavaHome}\bin");

        var path = PathSanitizer.Build(GetMachinePath(), prependPaths, includeNonOracle: true);
        set["PATH"] = path;

        return Assemble(OracleTool.RCU, set, clear, ctx);
    }

    /// <summary>
    /// Generic Oracle tool — apply base isolation (universal clears + JAVA_HOME + TEMP + PATH).
    /// </summary>
    private IsolatedEnvironmentVariables BuildGeneric(DeploymentEnvironmentContext ctx)
    {
        var clear = BuildClearList(ctx, clearWlst: ctx.ClearWlstResiduals, clearOpatch: ctx.ClearOpatchResiduals);

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            set["JAVA_HOME"] = ctx.JavaHome;
        if (!string.IsNullOrWhiteSpace(ctx.TempRoot))
        {
            set["TEMP"] = ctx.TempRoot;
            set["TMP"]  = ctx.TempRoot;
        }

        var prependPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.JavaHome))
            prependPaths.Add($@"{ctx.JavaHome}\bin");

        var path = PathSanitizer.Build(GetMachinePath(), prependPaths, includeNonOracle: true);
        set["PATH"] = path;

        return Assemble(OracleTool.Generic, set, clear, ctx);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static List<string> BuildClearList(
        DeploymentEnvironmentContext ctx,
        bool clearWlst,
        bool clearOpatch)
    {
        var clear = new List<string>(UniversalClearVars);
        if (clearWlst || ctx.ClearWlstResiduals)
            clear.AddRange(WlstResidualVars);
        if (clearOpatch || ctx.ClearOpatchResiduals)
            clear.AddRange(OpatchResidualVars);
        return clear;
    }

    private static IsolatedEnvironmentVariables Assemble(
        OracleTool tool,
        Dictionary<string, string> set,
        List<string> clear,
        DeploymentEnvironmentContext ctx)
    {
        // Remove from the clear list any variable that we're explicitly setting
        // (pointless to remove-then-set; the set takes precedence)
        var effectiveClear = clear
            .Where(v => !set.ContainsKey(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        var preamble = BuildPreamble(set, effectiveClear);

        var summary = new StringBuilder();
        summary.Append($"[{tool}] ");
        summary.Append($"set={set.Count}({string.Join(',', set.Keys)}) ");
        summary.Append($"clear={effectiveClear.Count}");

        return new IsolatedEnvironmentVariables
        {
            Tool                = tool,
            SetVariables        = set.AsReadOnly(),
            ClearVariables      = effectiveClear,
            PowerShellPreamble  = preamble,
            DiagnosticSummary   = summary.ToString(),
            BuiltAt             = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Builds the PowerShell preamble block:
    ///   Remove-Item Env:VAR -ErrorAction SilentlyContinue  (for each clear var)
    ///   $env:VAR = 'value'                                  (for each set var)
    /// </summary>
    public static string BuildPreamble(
        IReadOnlyDictionary<string, string> setVars,
        IReadOnlyList<string> clearVars)
    {
        var sb = new StringBuilder();

        // Clear stale variables first
        foreach (var name in clearVars)
            sb.AppendLine($"Remove-Item Env:{name} -ErrorAction SilentlyContinue");

        // Inject required variables
        foreach (var (name, value) in setVars)
        {
            var escaped = value.Replace("'", "''", StringComparison.Ordinal);
            sb.AppendLine($"$env:{name} = '{escaped}'");
        }

        return sb.ToString().TrimEnd();
    }

    private static string GetMachinePath()
    {
        // On Windows: read the current process PATH (which already incorporates machine + user PATH)
        // We sanitize it — stale Oracle entries will be stripped
        return Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    }
}
