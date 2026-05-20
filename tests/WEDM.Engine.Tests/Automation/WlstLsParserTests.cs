using FluentAssertions;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Automation;
using Xunit;

namespace WEDM.Engine.Tests.Automation;

// ═══════════════════════════════════════════════════════════════════════════════
// WlstLsParserTests
// ═══════════════════════════════════════════════════════════════════════════════
//
// Regression tests for _wedm_ls() — two bugs, two fix iterations.
//
// BUG 1 (original)
// ────────────────
// result.append(s) appended the raw "drw-    Security" line.
// 'Security' not in ['drw-    Security'] → True → discovery aborted.
// Fix: use s.split() and take parts[1].
//
// BUG 2 (second iteration — exposed in production)
// ────────────────────────────────────────────────
// On WLS 12c (Jython 2.5), ls() returns a SINGLE MULTILINE STRING, not an array.
//
//   raw = "drw-    Credential\ndrw-    Security\n..."
//
// "for i in raw:" on a Python STRING iterates CHARACTER BY CHARACTER:
//   i = 'd', 'r', 'w', '-', ' ', 'S', 'e', 'c', ...
//
// str('d').split() = ['d'] → len == 1 → result.append('d')
// str(' ').strip() = '' → skipped
//
// Result was a list of individual characters.  'Security' never found.
// Diagnostic logs showed the WLST output (printed to stdout by ls()) but
// the Python variable raw held the same content as one big string.
//
// FIX (iteration 3)
// ─────────────────
// Normalise raw into a list of lines BEFORE iterating:
//   1. raw is None → return []
//   2. raw.splitlines() works → Python str or Java String (Jython wraps it)
//   3. splitlines() raises → iterate raw directly (Java array/collection)
//   4. both raise → str(raw).splitlines() as final fallback
// Then parse each line: s.split() → parts[1] (name after permission prefix).
//
// THREE TEST CLASSES
// ──────────────────
// 1. WlstLsParserLogicTests      — line-by-line parsing logic (iterable path)
// 2. WlstLsMultilineStringTests  — multiline string normalisation (string path)
// 3. WlstLsParserGenerationTests — generated Python code content assertions
// ═══════════════════════════════════════════════════════════════════════════════

// ── 1. Parser algorithm tests ─────────────────────────────────────────────────

public sealed class WlstLsParserLogicTests
{
    // ── ParseWlstLsLines: C# mirror of the fixed _wedm_ls() Python logic ────

    /// <summary>
    /// Mirrors the fixed _wedm_ls() Python function exactly:
    ///   for each line: strip, split(), take parts[1] if ≥2 tokens else parts[0].
    /// </summary>
    private static List<string> ParseWlstLsLines(IEnumerable<string> rawLines)
    {
        var result = new List<string>();
        foreach (var line in rawLines)
        {
            var s = line.Trim();
            if (string.IsNullOrEmpty(s))
                continue;
            var parts = s.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                result.Add(parts[1]);
            else if (parts.Length == 1)
                result.Add(parts[0]);
        }
        return result;
    }

    // ── Core correctness: drw- directory lines ────────────────────────────────

    /// <summary>
    /// THE PRIMARY REGRESSION.
    ///
    /// Proves that the fixed parser extracts "Security" from "drw-    Security"
    /// so that the check 'Security' in _root succeeds and cd('/Security') is reached.
    /// </summary>
    [Fact]
    public void Parser_drw_line_returns_name_not_full_line()
    {
        var lines = new[] { "drw-    Security" };
        var result = ParseWlstLsLines(lines);

        result.Should().Contain("Security",
            "The parser must extract 'Security' from 'drw-    Security'. " +
            "Before the fix, the full line 'drw-    Security' was appended, causing " +
            "the 'Security' not in _root check to fail with " +
            "[WLST-DIAG] /Security not found at root.");

        result.Should().NotContain("drw-    Security",
            "The raw formatted line must never appear in the result list");

        result.Should().NotContain("drw-",
            "The permission prefix 'drw-' must be stripped");
    }

    /// <summary>
    /// Real WLST ls('/') output from a WLS 12c domain creation run.
    /// These are the EXACT lines observed in the failed deployment logs.
    /// </summary>
    [Fact]
    public void Parser_real_wls12c_root_ls_output()
    {
        // Exact ls('/') output observed at runtime from WLS 12c wls.jar template
        var realWlstOutput = new[]
        {
            "drw-   Credential",
            "drw-   Keystore",
            "drw-   NMProperties",
            "drw-   Security",
            "drw-   SecurityConfiguration",
            "drw-   Server",
            "drw-   StartupGroupConfig",
        };

        var result = ParseWlstLsLines(realWlstOutput);

        result.Should().Contain("Security",
            "'Security' must be present — cd('/Security') depends on it");
        result.Should().Contain("Credential");
        result.Should().Contain("Keystore");
        result.Should().Contain("NMProperties");
        result.Should().Contain("SecurityConfiguration");
        result.Should().Contain("Server");
        result.Should().Contain("StartupGroupConfig");
        result.Should().HaveCount(7,
            "All 7 directory entries must be parsed, no duplicates, no extras");
    }

    /// <summary>
    /// Attribute lines (-rw-) also appear in ls() output.
    /// Parser must correctly extract the attribute name and discard the value.
    /// </summary>
    [Fact]
    public void Parser_rw_attribute_line_returns_name_not_value()
    {
        var lines = new[]
        {
            "-rw-    Active                           false",
            "-rw-    AdminServerName                  AdminServer",
            "-rw-    AdministrationPort               9002",
            "-rw-    AdministrationPortEnabled        false",
            "-rw-    ListenPort                       7001",
        };

        var result = ParseWlstLsLines(lines);

        result.Should().Contain("Active",
            "'-rw-    Active false' must yield 'Active', not 'false'");
        result.Should().Contain("AdminServerName");
        result.Should().Contain("AdministrationPort");
        result.Should().Contain("AdministrationPortEnabled");
        result.Should().Contain("ListenPort");

        result.Should().NotContain("false",
            "Attribute values must not appear in the name list");
        result.Should().NotContain("AdminServer",
            "The value 'AdminServer' after AdminServerName must be discarded");
        result.Should().NotContain("9002",
            "Port numbers must be discarded — they are values, not names");
        result.Should().NotContain("-rw-",
            "Permission prefix '-rw-' must be stripped");
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Parser_blank_lines_are_skipped()
    {
        var lines = new[] { "", "drw-   Security", "   ", "\t", "drw-   Server" };
        var result = ParseWlstLsLines(lines);

        result.Should().HaveCount(2);
        result.Should().Contain("Security");
        result.Should().Contain("Server");
    }

    [Fact]
    public void Parser_single_token_lines_are_preserved()
    {
        // Some WLST versions or contexts might return just the name without permissions.
        // The parser falls back to parts[0] for single-token lines.
        var lines = new[] { "Security", "Server" };
        var result = ParseWlstLsLines(lines);

        result.Should().Contain("Security",
            "A single-token line must use parts[0] as fallback");
        result.Should().Contain("Server");
    }

    [Fact]
    public void Parser_tabs_and_variable_spacing_are_handled()
    {
        // WLST output may use tabs or variable numbers of spaces as separators.
        var lines = new[]
        {
            "drw-\tSecurity",              // tab separator
            "drw-  Credential",            // 2 spaces
            "drw-     Keystore",           // 5 spaces
            "-rw-\t\tListenPort\t\t7001",  // multiple tabs
        };

        var result = ParseWlstLsLines(lines);

        result.Should().Contain("Security");
        result.Should().Contain("Credential");
        result.Should().Contain("Keystore");
        result.Should().Contain("ListenPort");
        result.Should().HaveCount(4);
    }

    [Fact]
    public void Parser_empty_input_returns_empty_list()
    {
        var result = ParseWlstLsLines(Array.Empty<string>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parser_all_blank_lines_returns_empty_list()
    {
        var result = ParseWlstLsLines(new[] { "", "   ", "\t\t", "" });
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parser_mixed_drw_and_rw_lines()
    {
        // Mixed output as seen from ls('/Security/myrealm/User')
        var lines = new[]
        {
            "drw-   weblogic",
            "drw-   OracleSystemUser",
        };

        var result = ParseWlstLsLines(lines);
        result.Should().Contain("weblogic");
        result.Should().Contain("OracleSystemUser");
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Parser_realm_discovery_lines()
    {
        // After cd('/Security'), ls() returns the realm names.
        // This is critical: realm = realms[0] is used for the cmo.setPassword() path.
        var lines = new[]
        {
            "drw-   myrealm",
        };

        var result = ParseWlstLsLines(lines);

        result.Should().ContainSingle()
            .Which.Should().Be("myrealm",
                "Realm name 'myrealm' must be extracted so cd('myrealm/User') works correctly");

        result.Should().NotContain("drw-   myrealm",
            "Raw line must not appear — it would cause cd('drw-   myrealm/User') to fail");
    }

    [Fact]
    public void Parser_readonly_attributes_r_dash_dash()
    {
        // Read-only attributes use "-r--" prefix
        var lines = new[]
        {
            "-r--   Name   base_domain",
            "-r--   Type   Domain",
        };

        var result = ParseWlstLsLines(lines);
        result.Should().Contain("Name");
        result.Should().Contain("Type");
        result.Should().NotContain("base_domain");
        result.Should().NotContain("Domain");
    }

    [Fact]
    public void Parser_preserves_order()
    {
        var lines = new[]
        {
            "drw-   ZNode",
            "drw-   ANode",
            "drw-   MNode",
        };

        var result = ParseWlstLsLines(lines);
        result.Should().ContainInOrder("ZNode", "ANode", "MNode",
            "Parser must preserve the order returned by WLST ls()");
    }
}

// ── 2. Multiline string normalisation tests (Bug #2 regression) ──────────────

public sealed class WlstLsMultilineStringTests
{
    // ── ParseWlstLsOutput: C# mirror of the full three-path normalisation logic ─
    //
    // Mirrors the complete _wedm_ls() normalisation exactly:
    //   Path 1: raw is null/None      → return []
    //   Path 2: raw is a string       → raw.splitlines() → list of line strings
    //   Path 3: raw is IEnumerable    → iterate, str() each item → list of line strings
    //   Path 4: anything else         → raw.ToString().splitlines() → fallback
    // Then for each line: strip, split(), take parts[1] (≥2 tokens) or parts[0] (1 token).
    //
    // PATH 2 IS THE BUG #2 FIX PATH.
    // On WLS 12c (Jython 2.5), ls() returns a single multiline string.
    // "for i in a_string:" iterates CHARACTER BY CHARACTER — the old code's failure mode.
    // splitlines() on the string gives the correct list of formatted lines.

    private static List<string> ParseWlstLsOutput(object? raw)
    {
        if (raw is null)
            return new List<string>();

        IEnumerable<string> lines;

        if (raw is string str)
        {
            // Path 2: Python str.splitlines() — the WLS 12c multiline string case.
            // splitlines() splits on \n, \r\n, \r and returns a list, never iterating chars.
            lines = str.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        }
        else if (raw is IEnumerable<string> stringSeq)
        {
            // Path 3a: Jython list / Java array of strings — the WLS 14c case.
            lines = stringSeq;
        }
        else if (raw is System.Collections.IEnumerable objSeq)
        {
            // Path 3b: Java array / collection of non-string objects → str() each item.
            lines = objSeq.Cast<object>().Select(o => o?.ToString() ?? "");
        }
        else
        {
            // Path 4: scalar fallback — str(raw).splitlines() as final resort.
            lines = raw.ToString()!.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        }

        // Parse each line: s.split() → parts[1] (name after permission prefix).
        var result = new List<string>();
        foreach (var line in lines)
        {
            var s = line.Trim();
            if (string.IsNullOrEmpty(s))
                continue;
            var parts = s.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                result.Add(parts[1]);
            else if (parts.Length == 1)
                result.Add(parts[0]);
        }
        return result;
    }

    // ── Path 1: null (WLS 11g ls() returns None) ─────────────────────────────

    [Fact]
    public void Null_raw_returns_empty_list()
    {
        var result = ParseWlstLsOutput(null);
        result.Should().BeEmpty(
            "When ls() returns None (WLS 11g), _wedm_ls() must return [] not throw");
    }

    // ── Path 2: multiline string (WLS 12c — THE BUG #2 REGRESSION) ───────────

    /// <summary>
    /// THE PRIMARY BUG #2 REGRESSION TEST.
    ///
    /// On WLS 12c (Jython 2.5), ls() returns a SINGLE MULTILINE STRING:
    ///   raw = "drw-    Credential\ndrw-    Security\n..."
    ///
    /// The old code did: "for i in raw:" — iterates CHARACTER BY CHARACTER on a string.
    ///   i = 'd', 'r', 'w', '-', ' ', ...  →  str('d').split() = ['d']  →  append 'd'
    ///
    /// The fixed code uses: raw.splitlines() → ["drw-    Credential", "drw-    Security", ...]
    /// Then each line is parsed correctly: split() → parts[1] = "Security".
    /// </summary>
    [Fact]
    public void Multiline_string_root_ls_returns_correct_names()
    {
        // Exact ls('/') output from WLS 12c, returned as a single multiline string.
        // This is the exact format that triggered Bug #2 in production.
        const string raw = "drw-    Credential\ndrw-    Keystore\ndrw-    NMProperties\n" +
                           "drw-    Security\ndrw-    SecurityConfiguration\ndrw-    Server\n" +
                           "drw-    StartupGroupConfig\n";

        var result = ParseWlstLsOutput(raw);

        result.Should().Contain("Security",
            "'Security' must be found in the multiline string path. " +
            "Before the fix, for i in raw: iterated characters, never finding 'Security'.");
        result.Should().Contain("Credential");
        result.Should().Contain("Keystore");
        result.Should().Contain("NMProperties");
        result.Should().Contain("SecurityConfiguration");
        result.Should().Contain("Server");
        result.Should().Contain("StartupGroupConfig");
        result.Should().HaveCount(7,
            "All 7 directory entries must be parsed from the multiline string");

        result.Should().NotContain("d",
            "Individual characters must NOT appear — they were the Bug #2 symptom");
        result.Should().NotContain("r",
            "Permission prefix characters like 'r' must not appear in the result");
        result.Should().NotContain("drw-",
            "Permission prefix strings must not appear in the result");
    }

    [Fact]
    public void Multiline_string_with_crlf_line_endings()
    {
        // Windows-style CRLF line endings — must be handled by normalisation
        const string raw = "drw-    Security\r\ndrw-    Server\r\ndrw-    Credential\r\n";

        var result = ParseWlstLsOutput(raw);

        result.Should().Contain("Security");
        result.Should().Contain("Server");
        result.Should().Contain("Credential");
        result.Should().HaveCount(3);
    }

    [Fact]
    public void Multiline_string_with_trailing_newline()
    {
        // ls() output commonly ends with \n — the last element must not be an empty string
        const string raw = "drw-    Security\ndrw-    Server\n";

        var result = ParseWlstLsOutput(raw);

        result.Should().HaveCount(2, "Trailing newline must not produce an empty entry");
        result.Should().Contain("Security");
        result.Should().Contain("Server");
    }

    [Fact]
    public void Multiline_string_with_blank_lines_interspersed()
    {
        // Some WLST versions emit blank separator lines between entries
        const string raw = "\ndrw-    Security\n\ndrw-    Server\n\n";

        var result = ParseWlstLsOutput(raw);

        result.Should().HaveCount(2);
        result.Should().Contain("Security");
        result.Should().Contain("Server");
    }

    [Fact]
    public void Multiline_string_single_entry()
    {
        // Single realm under /Security — the typical production case
        const string raw = "drw-    myrealm\n";

        var result = ParseWlstLsOutput(raw);

        result.Should().ContainSingle()
            .Which.Should().Be("myrealm",
                "Single-entry multiline string must yield exactly one name");
    }

    [Fact]
    public void Multiline_string_attribute_lines_with_values()
    {
        // Attribute lines in a multiline string — values must be discarded
        const string raw =
            "-rw-    AdminServerName    AdminServer\n" +
            "-rw-    ListenPort         7001\n" +
            "drw-    Security\n";

        var result = ParseWlstLsOutput(raw);

        result.Should().Contain("AdminServerName");
        result.Should().Contain("ListenPort");
        result.Should().Contain("Security");
        result.Should().NotContain("AdminServer",
            "Attribute value 'AdminServer' is the third token and must be discarded");
        result.Should().NotContain("7001",
            "Port value '7001' is the third token and must be discarded");
        result.Should().HaveCount(3);
    }

    [Fact]
    public void Multiline_string_character_iteration_does_NOT_produce_correct_result()
    {
        // Proves the old "for i in raw:" approach is broken for multiline strings.
        // Simulates what the buggy code produced: single-char tokens from character iteration.
        const string raw = "drw-    Security\n";

        // Simulate the OLD buggy approach: iterate raw as a sequence of characters
        var buggyResult = new List<string>();
        foreach (var ch in raw)
        {
            var s = ch.ToString().Trim();
            if (string.IsNullOrEmpty(s))
                continue;
            var parts = s.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                buggyResult.Add(parts[1]);
            else if (parts.Length == 1)
                buggyResult.Add(parts[0]);
        }

        // Verify the old approach NEVER produces "Security"
        buggyResult.Should().NotContain("Security",
            "The old character-iteration approach never produces 'Security' from a multiline string — " +
            "this proves why splitlines() normalisation is required");

        // Verify the fixed approach DOES produce "Security"
        var fixedResult = ParseWlstLsOutput(raw);
        fixedResult.Should().Contain("Security",
            "The fixed splitlines() approach correctly extracts 'Security' from the same input");
    }

    // ── Path 3: IEnumerable / Java array (WLS 14c) ───────────────────────────

    [Fact]
    public void String_array_input_returns_correct_names()
    {
        // WLS 14c (Jython 2.7) may return a Java array.
        // Simulated as IEnumerable<string> — the iteration path.
        var raw = new[]
        {
            "drw-    Credential",
            "drw-    Security",
            "drw-    Server",
        };

        var result = ParseWlstLsOutput(raw);

        result.Should().Contain("Security");
        result.Should().Contain("Credential");
        result.Should().Contain("Server");
        result.Should().HaveCount(3);
    }

    [Fact]
    public void Object_enumerable_input_returns_correct_names()
    {
        // Simulates a Java object array where each element is a formatted ls() line.
        // The iteration path calls str() on each item before parsing.
        var raw = new object[]
        {
            "drw-    Security",
            "drw-    Server",
        };

        var result = ParseWlstLsOutput(raw);

        result.Should().Contain("Security");
        result.Should().Contain("Server");
        result.Should().HaveCount(2);
    }

    // ── Path 4: scalar fallback ───────────────────────────────────────────────

    [Fact]
    public void Scalar_object_toString_fallback_returns_correct_names()
    {
        // An object whose ToString() returns a multiline WLST ls() dump.
        // This is the str(raw).splitlines() last-resort path.
        var raw = new ToStringWrapper("drw-    Security\ndrw-    Server\n");

        var result = ParseWlstLsOutput(raw);

        result.Should().Contain("Security");
        result.Should().Contain("Server");
        result.Should().HaveCount(2);
    }

    // ── Real WLS 12c realm/user discovery sequences ───────────────────────────

    [Fact]
    public void Multiline_string_security_realm_discovery_succeeds()
    {
        // After cd('/Security'), ls() on WLS 12c returns a single multiline string.
        // This is the realm-discovery step: realm = realms[0] must be "myrealm".
        const string raw = "drw-    myrealm\n";

        var result = ParseWlstLsOutput(raw);

        result.Should().ContainSingle()
            .Which.Should().Be("myrealm",
                "Realm 'myrealm' must be extracted so cd('myrealm/User') works. " +
                "Before the fix, result was ['d','r','w','-','m','y','r','e','a','l','m'].");
    }

    [Fact]
    public void Multiline_string_user_discovery_under_realm()
    {
        // After cd('/Security/myrealm/User'), ls() returns user names.
        // configured_user in _users must succeed for the primary user path.
        const string raw = "drw-    weblogic\ndrw-    OracleSystemUser\n";

        var result = ParseWlstLsOutput(raw);

        result.Should().Contain("weblogic",
            "'weblogic' must be found so configured_user in _users is True");
        result.Should().Contain("OracleSystemUser");
        result.Should().HaveCount(2);
    }

    // ── Nested helper: simulates object whose ToString() returns WLST output ──

    private sealed class ToStringWrapper
    {
        private readonly string _value;
        public ToStringWrapper(string value) => _value = value;
        public override string ToString() => _value;
    }
}

// ── 3. Generated script content tests ────────────────────────────────────────

public sealed class WlstLsParserGenerationTests
{
    private static DeploymentConfiguration Make12cConfig() => new()
    {
        WebLogicVersion = WebLogicVersion.WLS_12c,
        Paths = new PathConfiguration
        {
            MiddlewareHome = @"D:\Oracle\Oracle_MW",
            DomainBase     = @"D:\Oracle\Oracle_MW\user_projects\domains",
            TempDirectory  = @"D:\Temp\wedm",
        },
        Domain = new DomainConfiguration
        {
            DomainName      = "wls_domain",
            AdminServerName = "AdminServer",
            AdminUsername   = "weblogic",
            AdminPassword   = "Welcome1",
            AdminPort       = 7001,
        },
        Network         = new NetworkConfiguration { Hostname = "localhost" },
        DomainHardening = new DomainHardeningConfiguration { ProductionMode = false },
    };

    // ── Verify generated Python contains the parsing fix ─────────────────────

    /// <summary>
    /// THE PRIMARY REGRESSION TEST FOR GENERATED CODE.
    ///
    /// Verifies that the emitted _wedm_ls() function uses s.split() to parse
    /// WLST ls() output lines, NOT the old result.append(s) raw line append.
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_uses_split_to_parse_ls_output()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        ctx.ScriptContent.Should().Contain("parts = s.split()",
            "_wedm_ls() must call s.split() to parse the WLST ls() output line. " +
            "Without this, 'drw-    Security' is not parsed to 'Security' and " +
            "cd('/Security') is never reached.");

        ctx.ScriptContent.Should().Contain("parts[1]",
            "_wedm_ls() must extract parts[1] — the name token after the permission prefix. " +
            "'drw-    Security'.split() = ['drw-', 'Security'] → parts[1] = 'Security'.");
    }

    /// <summary>
    /// Verifies the old broken implementation is gone.
    /// The old code used: result.append(s) where s = "drw-    Security" (whole line).
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_does_not_append_raw_line()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        // The old buggy pattern was: "if s:" followed by "result.append(s)"
        // where s is the raw "drw-    Security" line. After the fix, the code
        // must check `if not s: continue` (skipping blanks) and then parse.
        ctx.ScriptContent.Should().Contain("if not s:",
            "_wedm_ls() must skip blank lines with 'if not s: continue'");

        ctx.ScriptContent.Should().Contain("result.append(parts[1])",
            "_wedm_ls() must append parts[1] (the parsed name), not the raw line");

        // The raw append pattern must NOT appear in the _wedm_ls() function body.
        // Find the function block and verify it doesn't contain the old append.
        var functionStart = ctx.ScriptContent.IndexOf("def _wedm_ls():", StringComparison.Ordinal);
        var functionEnd   = ctx.ScriptContent.IndexOf("def _wedm_discover_admin_path(", StringComparison.Ordinal);
        functionStart.Should().BeGreaterThan(-1, "def _wedm_ls(): must exist in the script");
        functionEnd.Should().BeGreaterThan(functionStart, "_wedm_discover_admin_path must follow _wedm_ls");

        var functionBody = ctx.ScriptContent.Substring(functionStart, functionEnd - functionStart);

        // "result.append(s)" where s is the raw line — this is the bug.
        // The only result.append call must use parts[1] or parts[0], not bare s.
        functionBody.Should().NotContain("result.append(s)",
            "The old 'result.append(s)' raw-line append must not exist in the fixed _wedm_ls(). " +
            "It would add 'drw-    Security' instead of 'Security' to the result list.");
    }

    /// <summary>
    /// Verifies that the fallback for single-token lines (parts[0]) is present.
    /// Handles hypothetical WLST builds that return plain names without permission prefixes.
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_has_single_token_fallback()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        ctx.ScriptContent.Should().Contain("elif len(parts) == 1:",
            "_wedm_ls() must handle single-token lines as a fallback case");
        ctx.ScriptContent.Should().Contain("result.append(parts[0])",
            "_wedm_ls() must use parts[0] when a line has no permission prefix");
    }

    /// <summary>
    /// All four WLS versions should emit the same fixed _wedm_ls() implementation.
    /// </summary>
    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_all_versions_have_fixed_wedm_ls(WebLogicVersion version)
    {
        var config   = version == WebLogicVersion.WLS_11g
            ? new DeploymentConfiguration
            {
                WebLogicVersion = WebLogicVersion.WLS_11g,
                Paths = new PathConfiguration
                {
                    MiddlewareHome = @"D:\Oracle\Oracle_MW_11g",
                    DomainBase     = @"D:\Oracle\Oracle_MW_11g\user_projects\domains",
                    TempDirectory  = @"D:\Temp\wedm",
                },
                Domain = new DomainConfiguration
                {
                    DomainName = "d", AdminServerName = "AdminServer",
                    AdminUsername = "weblogic", AdminPassword = "pw", AdminPort = 7001,
                },
                Network = new NetworkConfiguration { Hostname = "h" },
                DomainHardening = new DomainHardeningConfiguration { ProductionMode = false },
            }
            : Make12cConfig();
        config.WebLogicVersion = version;

        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().Contain("parts = s.split()",
            $"{version} generated script must use s.split() in _wedm_ls()");
        ctx.ScriptContent.Should().Contain("result.append(parts[1])",
            $"{version} generated script must append parts[1], not the raw line");
        ctx.ScriptContent.Should().NotContain("result.append(s)",
            $"{version} generated script must not contain the old raw-line append");
    }

    /// <summary>
    /// End-to-end: both compatibility validators still pass after the _wedm_ls() change.
    /// The new implementation uses only Jython 2.2-compatible constructs.
    /// </summary>
    [Theory]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_with_fixed_wedm_ls_passes_both_validators(WebLogicVersion version)
    {
        var config   = Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        var apiReport    = WlstCompatibilityValidator.Validate(ctx.ScriptContent, version);
        var jythonReport = WlstJythonCompatibilityValidator.Validate(ctx.ScriptContent);

        apiReport.IsCompatible.Should().BeTrue(
            $"Fixed _wedm_ls() must not introduce API compatibility violations in {version} script. " +
            $"Violations: {string.Join("; ", apiReport.Violations)}");

        jythonReport.IsCompatible.Should().BeTrue(
            $"Fixed _wedm_ls() must use only Jython 2.2-compatible constructs. " +
            $"Violations: {string.Join("; ", jythonReport.Violations.Select(v => v.ToString()))}");
    }

    // ── Multiline string normalisation: generated code structure ─────────────

    /// <summary>
    /// Verifies that the generated _wedm_ls() calls raw.splitlines() to normalise
    /// the WLS 12c multiline-string return type into a list of lines.
    ///
    /// This is the root fix for Bug #2: without splitlines(), "for i in raw:" on a
    /// Python string iterates character by character, never producing "Security".
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_calls_splitlines()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        ctx.ScriptContent.Should().Contain("raw.splitlines()",
            "_wedm_ls() must normalise the WLS 12c multiline-string return type with " +
            "raw.splitlines() before iterating. Without this, 'for i in raw:' on a " +
            "Python string iterates character by character and 'Security' is never found.");
    }

    /// <summary>
    /// Verifies that the generated _wedm_ls() contains a try/except around splitlines()
    /// to fall back to iteration for Java arrays (WLS 14c) that don't have splitlines().
    ///
    /// The except must use the Jython 2.2 comma form: "except Exception, _ex1:"
    /// NOT "except Exception as _ex1:" (Python 3 / Jython 2.5+ only).
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_has_nested_tryexcept_for_splitlines_fallback()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        // The outer except for the splitlines() path — Jython 2.2 comma form required
        ctx.ScriptContent.Should().Contain("except Exception, _ex1:",
            "_wedm_ls() must catch splitlines() failure with Jython 2.2 comma form. " +
            "'except Exception as _ex1:' is a SyntaxError on Jython 2.2 (WLS 11g).");

        // The inner except for the iteration fallback path
        ctx.ScriptContent.Should().Contain("except Exception, _ex2:",
            "_wedm_ls() must have a nested except for the iteration fallback path. " +
            "Two except clauses with Jython 2.2 comma form provide the three-path fallback.");

        // MUST NOT use Python 3 'as' form for these variables
        ctx.ScriptContent.Should().NotContain("except Exception as _ex1:",
            "Python 3 'as' form must not appear — Jython 2.2 (WLS 11g) parses it as SyntaxError");
        ctx.ScriptContent.Should().NotContain("except Exception as _ex2:",
            "Python 3 'as' form must not appear — Jython 2.2 (WLS 11g) parses it as SyntaxError");
    }

    /// <summary>
    /// Verifies that the iteration fallback (for _item in raw) is inside the second
    /// except block, not at the top level of _wedm_ls().
    ///
    /// The fallback loop "for _item in raw: _lines.append(str(_item))" handles
    /// Java arrays/collections (WLS 14c) that don't have splitlines().
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_has_iteration_fallback_for_java_arrays()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        ctx.ScriptContent.Should().Contain("for _item in raw:",
            "_wedm_ls() must include a 'for _item in raw:' fallback for Java array return types " +
            "(WLS 14c+). This path activates only when raw.splitlines() raises an exception.");

        ctx.ScriptContent.Should().Contain("_lines.append(str(_item))",
            "_wedm_ls() fallback must call str(_item) on each Java array element and " +
            "append to _lines. str() wraps Java String objects as Python strings.");
    }

    /// <summary>
    /// Verifies that the final scalar fallback (str(raw).splitlines()) is present.
    /// This handles any unexpected raw type that is neither a string nor iterable.
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_has_scalar_fallback()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        ctx.ScriptContent.Should().Contain("str(raw).splitlines()",
            "_wedm_ls() must have a final fallback: str(raw).splitlines() for any " +
            "unexpected raw type. This ensures _wedm_ls() never raises unexpectedly.");
    }

    // ── Diagnostics: generated WLST-DIAG print statements ────────────────────

    /// <summary>
    /// Verifies that the generated _wedm_ls() prints the raw type from ls().
    /// This diagnostic was essential for identifying Bug #2: the log revealed
    /// that raw was a &lt;type 'str'&gt; (multiline string), not a Java array.
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_prints_raw_type_diagnostic()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        ctx.ScriptContent.Should().Contain("[WLST-DIAG] ls() raw type:",
            "_wedm_ls() must print the raw ls() return type. " +
            "This diagnostic identified the Bug #2 root cause: " +
            "raw was <type 'str'>, not a Java array as assumed.");
    }

    /// <summary>
    /// Verifies that the generated _wedm_ls() prints the raw value (first 200 chars).
    /// The truncation [:200] prevents flooding logs for large ls() outputs.
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_prints_raw_value_diagnostic_truncated()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        ctx.ScriptContent.Should().Contain("[WLST-DIAG] ls() raw value:",
            "_wedm_ls() must print the raw ls() value for post-mortem debugging");

        ctx.ScriptContent.Should().Contain("str(raw)[:200]",
            "_wedm_ls() must truncate the raw value to 200 chars to prevent log flooding");
    }

    /// <summary>
    /// Verifies that the generated _wedm_ls() prints the final parsed result.
    /// SUCCESS CRITERION: deployment logs must show
    ///   [WLST-DIAG] Parsed ls: ['Credential', 'Keystore', 'Security', ...]
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_prints_parsed_result_diagnostic()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        ctx.ScriptContent.Should().Contain("[WLST-DIAG] Parsed ls:",
            "_wedm_ls() must print the final parsed result so deployment logs show " +
            "[WLST-DIAG] Parsed ls: ['Credential', 'Keystore', 'Security', ...] " +
            "confirming that name extraction succeeded before discovery logic runs.");
    }

    /// <summary>
    /// All three diagnostic messages must appear in every WLS version's generated script.
    /// Diagnostics must not be version-gated.
    /// </summary>
    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_all_versions_have_all_three_diagnostics(WebLogicVersion version)
    {
        var config = version == WebLogicVersion.WLS_11g
            ? new DeploymentConfiguration
            {
                WebLogicVersion = WebLogicVersion.WLS_11g,
                Paths = new PathConfiguration
                {
                    MiddlewareHome = @"D:\Oracle\Oracle_MW_11g",
                    DomainBase     = @"D:\Oracle\Oracle_MW_11g\user_projects\domains",
                    TempDirectory  = @"D:\Temp\wedm",
                },
                Domain = new DomainConfiguration
                {
                    DomainName = "d", AdminServerName = "AdminServer",
                    AdminUsername = "weblogic", AdminPassword = "pw", AdminPort = 7001,
                },
                Network = new NetworkConfiguration { Hostname = "h" },
                DomainHardening = new DomainHardeningConfiguration { ProductionMode = false },
            }
            : Make12cConfig();
        config.WebLogicVersion = version;

        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().Contain("[WLST-DIAG] ls() raw type:",
            $"{version}: raw type diagnostic must be present");
        ctx.ScriptContent.Should().Contain("[WLST-DIAG] ls() raw value:",
            $"{version}: raw value diagnostic must be present");
        ctx.ScriptContent.Should().Contain("[WLST-DIAG] Parsed ls:",
            $"{version}: parsed result diagnostic must be present (SUCCESS CRITERION)");
    }

    /// <summary>
    /// Verifies that the _wedm_ls() function body contains the _lines variable —
    /// the normalised list of line strings produced by one of the three paths.
    /// The _lines variable bridges normalisation and parsing in the fixed implementation.
    /// </summary>
    [Fact]
    public void GeneratedScript_wedm_ls_uses_lines_intermediate_variable()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        var functionStart = ctx.ScriptContent.IndexOf("def _wedm_ls():", StringComparison.Ordinal);
        var functionEnd   = ctx.ScriptContent.IndexOf("def _wedm_discover_admin_path(", StringComparison.Ordinal);
        functionStart.Should().BeGreaterThan(-1);
        functionEnd.Should().BeGreaterThan(functionStart);

        var functionBody = ctx.ScriptContent.Substring(functionStart, functionEnd - functionStart);

        functionBody.Should().Contain("_lines = []",
            "_wedm_ls() must initialise _lines as an empty list before any normalisation path");
        functionBody.Should().Contain("_lines = raw.splitlines()",
            "_wedm_ls() must assign _lines from raw.splitlines() in the string path");
        functionBody.Should().Contain("for _line in _lines:",
            "_wedm_ls() must iterate _lines (the normalised line list), not raw directly");

        // The old bug: iterating raw directly without normalisation
        functionBody.Should().NotContain("for i in raw:",
            "The old 'for i in raw:' must not exist — iterates characters on strings");
        functionBody.Should().NotContain("for _line in raw:",
            "Iterating raw directly is the Bug #2 pattern and must not exist in the fixed code");
    }
}
