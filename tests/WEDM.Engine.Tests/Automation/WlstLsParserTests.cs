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
// Regression tests for the _wedm_ls() parser bug.
//
// ROOT CAUSE (preserved as documentation)
// ────────────────────────────────────────
// WLST ls() returns output lines formatted as:
//
//   "drw-    Security"
//   "drw-    Credential"
//   "-rw-    ListenPort    7001"
//   "-rw-    Active        false"
//
// The old _wedm_ls() implementation appended the WHOLE raw line to the result:
//
//   result.append(s)   ← "drw-    Security" (wrong)
//
// Then _wedm_discover_admin_path() checked:
//
//   if 'Security' not in _root:
//       raise Exception('[WLST-DIAG] /Security not found at root. ls=' + ...)
//
// The check failed because the list contained "drw-    Security", not "Security".
// Runtime log showed:
//
//   [WLST-DIAG] /Security not found at root.
//   ls=['drw-    Credential', 'drw-    Security', ...]
//
// FIX
// ───
// _wedm_ls() now splits each line by whitespace and takes parts[1] (the name):
//
//   "drw-    Security"         → "Security"
//   "-rw-    ListenPort 7001"  → "ListenPort"
//
// TWO TEST CLASSES
// ────────────────
// 1. WlstLsParserLogicTests   — tests the parsing algorithm directly via a C# mirror
//    of the Python function, without generating a WLST script.  Proves correctness
//    of the parsing logic against real WLST output samples.
//
// 2. WlstLsParserGenerationTests — tests that the GENERATED Python code inside the
//    domain creation script contains the correct parsing implementation and does NOT
//    contain the old broken append(s) pattern.
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

// ── 2. Generated script content tests ────────────────────────────────────────

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
}
