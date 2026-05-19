using WEDM.Domain.Enums;

namespace WEDM.Engine.Automation;

/// <summary>
/// Statically validates a WLST Python script against the target WebLogic version before
/// it is handed to wlst.cmd for execution.
///
/// Detecting problems at validate-time produces a clear, actionable log message and allows
/// the deployment to fail fast — before WLST starts the JVM, loads the template, and writes
/// partial domain files that must then be rolled back.
///
/// Known violations caught here
/// ────────────────────────────
/// • set('Password', ...) in 12c/14c — triggers ScriptException from checkSecurityInfo()
///   inside writeDomain().  Fix: cmo.setPassword() via MBean navigation.
/// • Missing cmo.setPassword() in 12c/14c — same checkSecurityInfo() failure path.
/// • Missing readTemplate() — WLST has no template loaded; all MBean navigation fails.
/// • Missing writeDomain()  — domain is never written to disk.
/// • Missing exit()         — WLST process stays open; watchdog kills it as a hang.
/// </summary>
public static class WlstCompatibilityValidator
{
    // ── Public entry point ────────────────────────────────────────────────────

    public static WlstCompatibilityReport Validate(string script, WebLogicVersion version)
    {
        ArgumentNullException.ThrowIfNull(script);

        var violations = new List<string>();
        var warnings   = new List<string>();

        CheckRequiredConstructs(script, violations, warnings);

        if (version >= WebLogicVersion.WLS_12c)
            Check12cApiRules(script, violations, warnings);
        else
            Check11gApiRules(script, warnings);

        CheckGeneralPatterns(script, warnings);

        return new WlstCompatibilityReport
        {
            IsCompatible = violations.Count == 0,
            Violations   = violations.AsReadOnly(),
            Warnings     = warnings.AsReadOnly(),
        };
    }

    // ── Check groups ──────────────────────────────────────────────────────────

    /// <summary>Constructs that are required in every WLST offline domain creation script.</summary>
    private static void CheckRequiredConstructs(
        string script, List<string> violations, List<string> warnings)
    {
        if (!script.Contains("readTemplate(", StringComparison.Ordinal))
            violations.Add(
                "readTemplate() is missing. " +
                "The WLST session has no template loaded — all MBean navigation will fail.");

        if (!script.Contains("writeDomain(", StringComparison.Ordinal))
            violations.Add(
                "writeDomain() is missing. " +
                "The domain will never be materialised on disk.");

        if (!script.Contains("exit()", StringComparison.Ordinal))
            violations.Add(
                "exit() is missing. " +
                "After writeDomain() completes, WLST will wait at the interactive prompt. " +
                "The watchdog will eventually classify this as a hang and kill the process.");

        if (!script.Contains("closeTemplate()", StringComparison.Ordinal))
            warnings.Add(
                "closeTemplate() is missing. " +
                "Best practice: call closeTemplate() immediately after writeDomain() to release " +
                "the template file lock.");
    }

    /// <summary>
    /// Rules specific to WebLogic 12c and 14c.
    ///
    /// The critical rule: set('Password', ...) is NOT a valid attribute in 12c.
    /// WebLogic 12c's writeDomain() calls ScriptExecutor.checkSecurityInfo() which
    /// validates security realm attributes.  The raw 'Password' attribute is rejected,
    /// producing: "com.oracle.cie.domain.script.ScriptException: Attribute 'Password' is not valid"
    ///
    /// The correct 12c API navigates to the admin user MBean and calls cmo.setPassword():
    ///   cd('/Security/base_domain/User/weblogic')
    ///   cmo.setPassword('your_password')
    ///   cd('/')
    /// </summary>
    private static void Check12cApiRules(
        string script, List<string> violations, List<string> warnings)
    {
        // ── Hard violations ───────────────────────────────────────────────────

        if (ContainsPasswordSetAttribute(script))
            violations.Add(
                "set('Password', ...) [or set(\"Password\", ...)] is not a valid MBean attribute " +
                "in WebLogic 12c/14c.  writeDomain() calls checkSecurityInfo() which rejects " +
                "the raw 'Password' attribute, producing: " +
                "\"ScriptException: Attribute 'Password' is not valid\".\n" +
                "Fix: navigate to the admin user MBean and use cmo.setPassword():\n" +
                "  cd('/Security/base_domain/User/weblogic')\n" +
                "  cmo.setPassword('your_password')\n" +
                "  cd('/')");

        if (!script.Contains("cmo.setPassword(", StringComparison.Ordinal))
            violations.Add(
                "Admin password is not set before writeDomain() in a 12c/14c script. " +
                "writeDomain() will call checkSecurityInfo() and fail because the " +
                "template user has no usable password.\n" +
                "Fix: add before writeDomain():\n" +
                "  cd('/Security/base_domain/User/weblogic')\n" +
                "  cmo.setPassword('your_password')\n" +
                "  cd('/')");

        // ── Deprecation warnings ──────────────────────────────────────────────

        if (script.Contains("cmo.setUserPassword(", StringComparison.Ordinal))
            warnings.Add(
                "cmo.setUserPassword() is deprecated in 12c and removed in 14c. " +
                "Use cmo.setPassword() instead.");
    }

    /// <summary>Rules and informational notes for WebLogic 11g (10.3.x) scripts.</summary>
    private static void Check11gApiRules(string script, List<string> warnings)
    {
        // In 11g, set('Password', ...) may or may not work depending on the template and
        // patch level — it is not a hard violation but should be flagged.
        if (ContainsPasswordSetAttribute(script))
            warnings.Add(
                "set('Password', ...) was used.  In WebLogic 11g this may work, but it is " +
                "not the recommended API.  Prefer cmo.setPassword() for forward compatibility.");

        if (!script.Contains("cmo.setPassword(", StringComparison.Ordinal) &&
            !script.Contains("cmo.setUserPassword(", StringComparison.Ordinal))
            warnings.Add(
                "No explicit password setter found for 11g.  " +
                "WLST will use the template default password, which may cause subsequent " +
                "AdminServer boot failures if the template uses a locked or expired credential.");
    }

    /// <summary>Version-agnostic pattern checks.</summary>
    private static void CheckGeneralPatterns(string script, List<string> warnings)
    {
        if (!ContainsSetName(script))
            warnings.Add(
                "Domain name (set('Name', ...)) is not set explicitly. " +
                "The domain will use the template's built-in name, which may conflict with " +
                "other domains or with config.xml path expectations.");

        if (!script.Contains("setOption('OverwriteDomain'", StringComparison.Ordinal))
            warnings.Add(
                "setOption('OverwriteDomain', 'true') is not set. " +
                "If the target domain directory already exists, writeDomain() may fail.");

        // Hardcoded /Security/<name>/User/ path — breaks when the template's realm name differs.
        // The correct approach is dynamic discovery via ls() after readTemplate().
        // cd('/Security/') appearing as a literal string in the script indicates a hardcoded path.
        if (ContainsHardcodedSecurityPath(script))
            warnings.Add(
                "A hardcoded /Security/<realm>/User/<user> path was detected (e.g. cd('/Security/base_domain/User/weblogic')). " +
                "The realm name in wls.jar varies across WLS versions and patch levels — " +
                "hardcoding it causes cd() failures when the template uses a different name (e.g. 'myrealm'). " +
                "Use dynamic discovery: navigate /Security, list realms with ls(), then cd into the first realm/User/<user>.");
    }

    // ── Pattern helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the script contains a direct Password attribute setter in either
    /// single-quote or double-quote form:
    ///   set('Password', ...)   or   set("Password", ...)
    /// </summary>
    private static bool ContainsPasswordSetAttribute(string script)
        => script.Contains("set('Password'",  StringComparison.Ordinal)
        || script.Contains("set(\"Password\"", StringComparison.Ordinal);

    private static bool ContainsSetName(string script)
        => script.Contains("set('Name'",  StringComparison.Ordinal)
        || script.Contains("set(\"Name\"", StringComparison.Ordinal);

    /// <summary>
    /// Returns true if the script contains a literal cd('/Security/...') call with a
    /// hardcoded realm name — a pattern that breaks when the template uses a different name.
    /// Dynamic discovery uses cd(_realm + '/User') with a variable, not a string literal.
    /// </summary>
    private static bool ContainsHardcodedSecurityPath(string script)
        => script.Contains("cd('/Security/",  StringComparison.Ordinal)
        || script.Contains("cd(\"/Security/", StringComparison.Ordinal);
}
