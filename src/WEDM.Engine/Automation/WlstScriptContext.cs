using WEDM.Domain.Enums;

namespace WEDM.Engine.Automation;

/// <summary>
/// Immutable result of WLST domain script generation.
///
/// Carries both the script text and the metadata needed for diagnostics, logging, and
/// failed-script retention — so that if writeDomain() fails, the exact script, template,
/// and target path are available for post-mortem analysis.
/// </summary>
public sealed class WlstScriptContext
{
    /// <summary>Full Python script content, ready to be written to a .py file and passed to WLST.</summary>
    public string ScriptContent     { get; init; } = string.Empty;

    /// <summary>Absolute path to the WLS template jar (e.g. wls.jar or wls-spring.jar).</summary>
    public string TemplatePath      { get; init; } = string.Empty;

    /// <summary>Target domain directory that writeDomain() will produce on disk.</summary>
    public string DomainPath        { get; init; } = string.Empty;

    /// <summary>Admin username embedded in the script (e.g. "weblogic").</summary>
    public string AdminUser         { get; init; } = string.Empty;

    /// <summary>WebLogic version this script was generated for.</summary>
    public WebLogicVersion Version  { get; init; }

    /// <summary>ISO-8601 UTC timestamp of generation — embedded in the script header comment.</summary>
    public string GeneratedAt       { get; init; } = string.Empty;

    /// <summary>
    /// Internal domain name used in the WLST security realm path.
    ///
    /// For the standard wls.jar template this is always "base_domain".
    /// The realm path constructed by the script is:
    ///   /Security/{TemplateRealmName}/User/{AdminUser}
    ///
    /// This value comes from inside the JAR and does not change when you call
    /// <c>set('Name', domainName)</c> — renaming the domain does not rename the realm.
    /// </summary>
    public string TemplateRealmName { get; init; } = "base_domain";

    /// <summary>Produces the one-line [WLST] diagnostic string logged before execution.</summary>
    public string ToDiagnosticLine()
        => $"[WLST] Version={Version} Template={TemplatePath} Domain={DomainPath} "
         + $"AdminUser={AdminUser} GeneratedAt={GeneratedAt}";
}

/// <summary>
/// Result of a <see cref="WlstCompatibilityValidator"/> check against a WLST Python script.
/// </summary>
public sealed class WlstCompatibilityReport
{
    /// <summary>
    /// <c>true</c> when no violations were detected.
    /// Warnings are informational and do not affect this flag.
    /// </summary>
    public bool IsCompatible                       { get; init; }

    /// <summary>
    /// Hard API violations that will cause the WLST execution to fail.
    /// E.g.: <c>set('Password', ...)</c> in a 12c script.
    /// </summary>
    public IReadOnlyList<string> Violations        { get; init; } = [];

    /// <summary>
    /// Soft warnings: deprecated APIs, missing optional calls, etc.
    /// These may succeed but should be fixed for long-term compatibility.
    /// </summary>
    public IReadOnlyList<string> Warnings          { get; init; } = [];

    /// <summary>Human-readable one-line summary.</summary>
    public string Summary => IsCompatible
        ? $"Compatible — {Warnings.Count} warning(s)."
        : $"INCOMPATIBLE — {Violations.Count} violation(s), {Warnings.Count} warning(s).";
}
