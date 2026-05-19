using System.Text;

namespace WEDM.Engine.Automation;

/// <summary>
/// Jython code fragments emitted into WLST offline domain-creation scripts.
/// Shared by all version-specific providers.
///
/// Jython / Python compatibility guarantee
/// ────────────────────────────────────────
/// All emitted code must be valid Jython 2.2+ (WLS 11g minimum).
/// The following Python 3 / Python 2.5+ constructs are BANNED from this file:
///
///   BANNED                             REQUIRED REPLACEMENT
///   ──────────────────────────────     ──────────────────────────────────────
///   except Exception as e:             except Exception, e:
///   x if condition else y  (ternary)   if condition:\n    x = a\nelse:\n    x = b
///   f'...' / f"..."  (f-strings)       '...' + str(var)  string concatenation
///   print(a, b)  (multi-arg)           print a + ' ' + b  statement form
///
/// Jython version baseline per WLS release:
///   WLS 10.3.x (11g)  → Jython 2.2.1  (no ternary, no 'as' in except)
///   WLS 12.1.x/12.2.x → Jython 2.5.3+ (ternary OK, but 'as' in except still risky)
///   WLS 14.1.x        → Jython 2.7.x  (most Python 2.7 constructs OK)
///   WLS 15.x          → Jython 2.7.x
///
/// We target Jython 2.2.1 as the lowest common denominator so that a single
/// generated script works on ALL WLS versions without a runtime syntax error.
///
/// Dynamic realm discovery
/// ───────────────────────
/// After readTemplate() the WLST security realm name varies by template and
/// WLS patch level — it may be "myrealm", "base_domain", or a custom name.
/// These helpers discover the realm name at runtime rather than hardcoding it.
/// </summary>
internal static class WlstDomainScriptHelpers
{
    // ── Public helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Appends the _wedm_ls() and _wedm_discover_admin_path() Jython helper functions.
    ///
    /// MUST be called before AppendAdminCredentialBlock() and before readTemplate()
    /// so the functions are defined by the time they are invoked.
    ///
    /// All emitted code is Jython 2.2-compatible:
    ///   • No "except X as e:" — uses "except X, e:" form
    ///   • No ternary expressions — uses explicit if/else blocks
    ///   • No f-strings — uses string concatenation
    /// </summary>
    public static void AppendAdminDiscoveryHelpers(StringBuilder sb)
    {
        sb.AppendLine("# ── WEDM runtime helpers: dynamic realm/admin-user discovery ─────────────");
        sb.AppendLine("# Compatible with Jython 2.2+ (WLS 11g through 15c).");
        sb.AppendLine("# No Python 3 syntax: no 'except X as e', no ternary, no f-strings.");
        sb.AppendLine();

        // ── _wedm_ls() ─────────────────────────────────────────────────────────
        sb.AppendLine("def _wedm_ls():");
        sb.AppendLine("    \"\"\"Return WLST ls() as a plain Python list; handles Java arrays and Jython lists.\"\"\"");
        sb.AppendLine("    try:");
        sb.AppendLine("        raw = ls()");
        sb.AppendLine("        if raw is None:");
        sb.AppendLine("            return []");
        sb.AppendLine("        result = []");
        sb.AppendLine("        for i in raw:");
        sb.AppendLine("            s = str(i).strip()");
        sb.AppendLine("            if s:");
        sb.AppendLine("                result.append(s)");
        sb.AppendLine("        return result");
        // Bare "except:" is Jython 2.2 compatible; we don't need the exception object here.
        sb.AppendLine("    except:");
        sb.AppendLine("        return []");
        sb.AppendLine();

        // ── _wedm_discover_admin_path() ────────────────────────────────────────
        sb.AppendLine("def _wedm_discover_admin_path(configured_user):");
        sb.AppendLine("    \"\"\"");
        sb.AppendLine("    Discover the admin-user MBean path from the loaded template at runtime.");
        sb.AppendLine("    Returns e.g. '/Security/myrealm/User/weblogic'.");
        sb.AppendLine("    Prints [WLST-DIAG] lines at every step for post-mortem analysis.");
        sb.AppendLine("    \"\"\"");
        sb.AppendLine("    print('[WLST-DIAG] Admin MBean discovery start')");
        sb.AppendLine("    print('[WLST-DIAG] Configured user: ' + configured_user)");
        sb.AppendLine("    cd('/')");
        sb.AppendLine("    _root = _wedm_ls()");
        sb.AppendLine("    print('[WLST-DIAG] Root ls: ' + str(_root))");
        sb.AppendLine("    if 'Security' not in _root:");
        sb.AppendLine("        raise Exception('[WLST-DIAG] /Security not found at root. ls=' + str(_root) +");
        sb.AppendLine("                        ' — ensure readTemplate() was called before discovery.')");
        // Jython 2.2: use "except Exception, _e:" NOT "except Exception as _e:"
        sb.AppendLine("    try:");
        sb.AppendLine("        cd('/Security')");
        sb.AppendLine("    except Exception, _e:");
        sb.AppendLine("        raise Exception('[WLST-DIAG] cd(/Security) failed: ' + str(_e) +");
        sb.AppendLine("                        ' | root ls=' + str(_root))");
        sb.AppendLine("    _realms = _wedm_ls()");
        sb.AppendLine("    print('[WLST-DIAG] Security realms found: ' + str(_realms))");
        sb.AppendLine("    if not _realms:");
        sb.AppendLine("        cd('/')");
        sb.AppendLine("        raise Exception('[WLST-DIAG] No security realms found under /Security — template may not be loaded.')");
        sb.AppendLine("    _realm = _realms[0]");
        sb.AppendLine("    print('[WLST-DIAG] Using realm: ' + _realm)");
        sb.AppendLine("    try:");
        sb.AppendLine("        cd(_realm + '/User')");
        // Jython 2.2: use "except Exception, _e:" NOT "except Exception as _e:"
        sb.AppendLine("    except Exception, _e:");
        sb.AppendLine("        cd('/')");
        sb.AppendLine("        raise Exception('[WLST-DIAG] cd(/Security/' + _realm + '/User) failed: ' + str(_e))");
        sb.AppendLine("    _users = _wedm_ls()");
        sb.AppendLine("    print('[WLST-DIAG] Users found: ' + str(_users))");
        sb.AppendLine("    if not _users:");
        sb.AppendLine("        cd('/')");
        sb.AppendLine("        raise Exception('[WLST-DIAG] No users found under /Security/' + _realm + '/User')");
        // Jython 2.2: no ternary "x if cond else y" — use explicit if/else block instead
        sb.AppendLine("    if configured_user in _users:");
        sb.AppendLine("        _user = configured_user");
        sb.AppendLine("    else:");
        sb.AppendLine("        _user = _users[0]");
        sb.AppendLine("        print('[WLST-DIAG] WARNING: user ' + configured_user +");
        sb.AppendLine("              ' not in template; using: ' + _user)");
        sb.AppendLine("    _path = '/Security/' + _realm + '/User/' + _user");
        sb.AppendLine("    print('[WLST-DIAG] Resolved admin path: ' + _path)");
        sb.AppendLine("    cd('/')");
        sb.AppendLine("    return _path");
        sb.AppendLine("# ── end WEDM helpers ─────────────────────────────────────────────────────");
        sb.AppendLine();
    }

    /// <summary>
    /// Appends the admin credential block: discovers the admin-user MBean path at runtime,
    /// navigates to it, calls cmo.setPassword(), and returns to root.
    ///
    /// Must appear AFTER readTemplate() in the generated script.
    /// All emitted code is Jython 2.2-compatible.
    /// </summary>
    public static void AppendAdminCredentialBlock(StringBuilder sb, string adminUser, string adminPwd)
    {
        sb.AppendLine("# Admin credentials: dynamic discovery + cmo.setPassword()");
        sb.AppendLine("# (cmo.setPassword is the correct API for 12c/14c — NOT set('Password',...)");
        sb.AppendLine($"_admin_path = _wedm_discover_admin_path('{EscapePy(adminUser)}')");
        sb.AppendLine("print('[WLST-DIAG] Setting password at: ' + _admin_path)");
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
