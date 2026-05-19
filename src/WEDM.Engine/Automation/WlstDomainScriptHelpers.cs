using System.Text;

namespace WEDM.Engine.Automation;

/// <summary>
/// Jython code fragments emitted into WLST offline domain-creation scripts.
/// Shared by all version-specific providers (<see cref="Wls12cDomainScriptProvider"/>,
/// <see cref="Wls11gDomainScriptProvider"/>).
///
/// Dynamic realm discovery
/// ───────────────────────
/// WLST offline exposes the security configuration as an MBean subtree under /Security.
/// After readTemplate() the exact realm name varies:
///
///   • wls.jar on WLS 12.2.1.4  → realm may be 'myrealm' or 'base_domain'
///   • wls.jar on WLS 14.1.1     → realm is typically 'myrealm'
///   • Customer-customised templates may use any realm name
///
/// Hardcoding 'base_domain' in cd('/Security/base_domain/User/weblogic') therefore
/// breaks on any installation where the template's realm has a different name.
///
/// The correct approach is to navigate to /Security, list the available child MBeans
/// with ls(), and use whatever name is there — that is what these helpers do.
/// </summary>
internal static class WlstDomainScriptHelpers
{
    // ── Public helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Appends two Jython helper functions into the script StringBuilder.
    ///
    /// <c>_wedm_ls()</c>
    ///   Wraps WLST's ls() to return a plain Python list of strings.
    ///   WLST ls() returns a Java String[] in some versions and a Jython list in
    ///   others; the wrapper normalises both cases and filters empty strings.
    ///
    /// <c>_wedm_discover_admin_path(configured_user)</c>
    ///   After readTemplate() is called, navigates /Security, discovers the first
    ///   realm, finds the admin-user node, and returns the full MBean path.
    ///   Prints diagnostic [WLST-DIAG] lines at every step so failures are easy to
    ///   diagnose from the retained stdout artifact.
    ///   Prefers configured_user; falls back to the first available user.
    ///   Raises Exception with diagnostic detail if discovery fails.
    ///
    /// Call this BEFORE the readTemplate() line so the functions are defined before
    /// they are called.
    /// </summary>
    public static void AppendAdminDiscoveryHelpers(StringBuilder sb)
    {
        sb.AppendLine("# ── WEDM runtime helpers: dynamic realm / admin-user discovery ──────────");
        sb.AppendLine("def _wedm_ls():");
        sb.AppendLine("    \"\"\"Safely convert WLST ls() to a Python list (handles Java arrays and Jython lists).\"\"\"");
        sb.AppendLine("    try:");
        sb.AppendLine("        raw = ls()");
        sb.AppendLine("        if raw is None: return []");
        sb.AppendLine("        return [str(i) for i in raw if str(i).strip()]");
        sb.AppendLine("    except Exception: return []");
        sb.AppendLine();
        sb.AppendLine("def _wedm_discover_admin_path(configured_user):");
        sb.AppendLine("    \"\"\"");
        sb.AppendLine("    Dynamically discover the admin-user MBean path from the loaded template.");
        sb.AppendLine("    Returns e.g. '/Security/myrealm/User/weblogic'.");
        sb.AppendLine("    Raises Exception with diagnostic output if the path cannot be found.");
        sb.AppendLine("    \"\"\"");
        sb.AppendLine("    print('[WLST-DIAG] --- Admin MBean discovery start ---')");
        sb.AppendLine("    print('[WLST-DIAG] Configured user: ' + configured_user)");
        sb.AppendLine("    cd('/')");
        sb.AppendLine("    _root = _wedm_ls()");
        sb.AppendLine("    print('[WLST-DIAG] Root ls: ' + str(_root))");
        sb.AppendLine("    if 'Security' not in _root:");
        sb.AppendLine("        raise Exception('[WLST-DIAG] /Security not found at root. Root ls: ' + str(_root) +");
        sb.AppendLine("                        '. Ensure readTemplate() was called before discovery.')");
        sb.AppendLine("    try:");
        sb.AppendLine("        cd('/Security')");
        sb.AppendLine("    except Exception as _e:");
        sb.AppendLine("        raise Exception('[WLST-DIAG] cd(/Security) failed: ' + str(_e) +");
        sb.AppendLine("                        ' | Root ls: ' + str(_root))");
        sb.AppendLine("    _realms = _wedm_ls()");
        sb.AppendLine("    print('[WLST-DIAG] Security realms: ' + str(_realms))");
        sb.AppendLine("    if not _realms:");
        sb.AppendLine("        cd('/')");
        sb.AppendLine("        raise Exception('[WLST-DIAG] No realms found under /Security. ' +");
        sb.AppendLine("                        'Template may not be fully loaded.')");
        sb.AppendLine("    _realm = _realms[0]");
        sb.AppendLine("    print('[WLST-DIAG] Selected realm: ' + _realm)");
        sb.AppendLine("    try:");
        sb.AppendLine("        cd(_realm + '/User')");
        sb.AppendLine("    except Exception as _e:");
        sb.AppendLine("        cd('/')");
        sb.AppendLine("        raise Exception('[WLST-DIAG] cd(/Security/' + _realm + '/User) failed: ' + str(_e))");
        sb.AppendLine("    _users = _wedm_ls()");
        sb.AppendLine("    print('[WLST-DIAG] Available users: ' + str(_users))");
        sb.AppendLine("    if not _users:");
        sb.AppendLine("        cd('/')");
        sb.AppendLine("        raise Exception('[WLST-DIAG] No users found under /Security/' + _realm + '/User.')");
        sb.AppendLine("    _user = configured_user if configured_user in _users else _users[0]");
        sb.AppendLine("    if _user != configured_user:");
        sb.AppendLine("        print('[WLST-DIAG] WARNING: Configured user ' + configured_user +");
        sb.AppendLine("              ' not found; using first available: ' + _user)");
        sb.AppendLine("    _path = '/Security/' + _realm + '/User/' + _user");
        sb.AppendLine("    print('[WLST-DIAG] Resolved admin MBean path: ' + _path)");
        sb.AppendLine("    cd('/')");
        sb.AppendLine("    return _path");
        sb.AppendLine("# ── end WEDM helpers ────────────────────────────────────────────────────");
        sb.AppendLine();
    }

    /// <summary>
    /// Appends the admin credential block: calls <c>_wedm_discover_admin_path</c>,
    /// navigates to the discovered MBean, calls <c>cmo.setPassword()</c>, and returns
    /// to root.  Must be called after <c>readTemplate()</c> in the generated script.
    /// </summary>
    public static void AppendAdminCredentialBlock(StringBuilder sb, string adminUser, string adminPwd)
    {
        sb.AppendLine("# Admin credentials: dynamic realm/user discovery + cmo.setPassword()");
        sb.AppendLine($"_admin_path = _wedm_discover_admin_path('{EscapePy(adminUser)}')");
        sb.AppendLine("print('[WLST-DIAG] Setting admin password at: ' + _admin_path)");
        sb.AppendLine("cd(_admin_path)");
        sb.AppendLine($"cmo.setPassword('{EscapePy(adminPwd)}')");
        sb.AppendLine("cd('/')");
        sb.AppendLine("print('[WLST-DIAG] Admin password set.')");
        sb.AppendLine();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string EscapePy(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'",  StringComparison.Ordinal);
}
