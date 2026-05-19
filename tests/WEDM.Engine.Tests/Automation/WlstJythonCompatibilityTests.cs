using FluentAssertions;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Automation;
using Xunit;

namespace WEDM.Engine.Tests.Automation;

// ═══════════════════════════════════════════════════════════════════════════════
// WlstJythonCompatibilityTests
// ═══════════════════════════════════════════════════════════════════════════════
//
// Regression suite for the Jython 2.2 syntax compliance work.
//
// Root cause being guarded against
// ──────────────────────────────────────────────────────────────────────────────
// WLST embeds Jython, not CPython.  Jython 2.2.1 (WLS 11g) parses the ENTIRE
// script before executing any of it.  A single Python 3 construct anywhere in
// the file causes an immediate top-level SyntaxError with the misleading message:
//
//   Errors: Problem invoking WLST - Traceback (innermost last):
//     (no code object) at line 0
//     File "wedm_create_domain_...py",
//           except Exception as _e:
//                            ^
//     SyntaxError: invalid syntax
//
// The banned constructs are:
//   • "except X as e:"          → must be "except X, e:"
//   • "x if cond else y"        → must be an explicit if/else block (Jython 2.2)
//
// Two test classes:
//   1. WlstJythonCompatibilityValidatorTests  — unit tests the validator itself
//   2. WlstJythonRegressionTests              — asserts generated scripts are clean
// ═══════════════════════════════════════════════════════════════════════════════

// ── 1. Unit tests: WlstJythonCompatibilityValidator ──────────────────────────

public sealed class WlstJythonCompatibilityValidatorTests
{
    // ── except … as … — SyntaxError violations ────────────────────────────────

    [Fact]
    public void ExceptAsClause_Exception_IsSyntaxViolation()
    {
        const string script = "    except Exception as e:";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeFalse(
            "'except Exception as e:' is Python 3 / Py2.6+ syntax — Jython 2.2 rejects it " +
            "with a top-level SyntaxError at parse time, before any code executes");
        report.Violations.Should().HaveCount(1);
        report.Violations[0].Construct.Should().Contain("except");
        report.Violations[0].Severity.Should().Be(JythonViolationSeverity.SyntaxError);
    }

    [Fact]
    public void ExceptAsClause_SpecificType_IsSyntaxViolation()
    {
        const string script = "    except WLSTException as _e:";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeFalse("'except WLSTException as _e:' is equally invalid in Jython 2.2");
        report.Violations.Should().HaveCount(1);
        report.Violations[0].Severity.Should().Be(JythonViolationSeverity.SyntaxError);
    }

    [Fact]
    public void ExceptCommaForm_Jython22Style_IsCompatible()
    {
        // The CORRECT Jython 2.2 form — must NOT be flagged as a violation.
        const string script = "    except Exception, _e:";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue(
            "'except Exception, _e:' is the valid Python 2 / Jython 2.x form " +
            "and must not be reported as a violation");
        report.Violations.Should().BeEmpty();
    }

    [Fact]
    public void BareExcept_IsCompatible()
    {
        // Bare "except:" (no variable capture) is Jython 2.2 compatible.
        const string script = """
            try:
                ls()
            except:
                return []
            """;
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue("bare 'except:' is Jython 2.2-compatible");
        report.Violations.Should().BeEmpty();
    }

    [Fact]
    public void ExceptAsInsideComment_IsNotFlagged()
    {
        // Comment lines must be skipped entirely — the "#" prefix exempts the line.
        const string script = "# except Exception as e:  ← do not use this form";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue(
            "an 'except ... as ...' pattern inside a comment line must not produce a violation");
        report.Violations.Should().BeEmpty();
    }

    [Fact]
    public void ExceptAsInsideStringLiteral_IsNotFlagged()
    {
        // "as " appearing inside a string value is not a syntax construct.
        const string script = "msg = 'Use except Exception, e: not except Exception as e:'";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue(
            "' as ' inside a string literal is data, not Python syntax");
        report.Violations.Should().BeEmpty();
    }

    // ── Ternary expression — SyntaxError violation in Jython 2.2 ─────────────

    [Fact]
    public void TernaryExpression_IsSyntaxViolation()
    {
        const string script = "    _user = configured_user if configured_user in _users else _users[0]";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeFalse(
            "ternary 'x if cond else y' requires Python 2.5+ and causes a SyntaxError " +
            "in Jython 2.2 (WLS 11g)");
        report.Violations.Should().HaveCount(1);
        report.Violations[0].Construct.Should().Contain("ternary");
        report.Violations[0].Severity.Should().Be(JythonViolationSeverity.SyntaxError);
    }

    [Fact]
    public void RegularIfStatement_IsCompatible()
    {
        // A standalone "if" at the start of a line is a normal if-statement, not a ternary.
        const string script = """
            if configured_user in _users:
                _user = configured_user
            else:
                _user = _users[0]
            """;
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue(
            "explicit if/else blocks are the correct Jython 2.2 replacement for ternary expressions");
        report.Violations.Should().BeEmpty();
    }

    [Fact]
    public void ElifStatement_IsNotTernary()
    {
        const string script = """
            if x:
                pass
            elif y:
                pass
            else:
                pass
            """;
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue("elif/else blocks are not ternary expressions");
        report.Violations.Should().BeEmpty();
    }

    [Fact]
    public void TernaryInsideStringLiteral_IsNotFlagged()
    {
        const string script = "msg = 'Do not use x if cond else y in Jython 2.2'";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue("ternary-like text inside a string is not a syntax construct");
        report.Violations.Should().BeEmpty();
    }

    // ── f-strings — Warning (not violation) ───────────────────────────────────

    [Fact]
    public void FStringSingleQuote_IsWarning()
    {
        const string script = "print(f'Hello {name}')";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue("f-strings produce a Warning, not a SyntaxError violation");
        report.Violations.Should().BeEmpty();
        report.Warnings.Should().HaveCount(1);
        report.Warnings[0].Severity.Should().Be(JythonViolationSeverity.Warning);
        report.Warnings[0].Construct.Should().Contain("f-string");
    }

    [Fact]
    public void FStringDoubleQuote_IsWarning()
    {
        const string script = "print(f\"Hello {name}\")";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue();
        report.Warnings.Should().HaveCount(1);
        report.Warnings[0].Construct.Should().Contain("f-string");
    }

    // ── Walrus operator — Warning ──────────────────────────────────────────────

    [Fact]
    public void WalrusOperator_IsWarning()
    {
        const string script = "if (n := len(data)) > 10:";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue("walrus operator produces a Warning, not a violation");
        report.Violations.Should().BeEmpty();
        report.Warnings.Should().Contain(w => w.Construct.Contains("walrus"));
    }

    // ── Multi-argument print() — Warning ──────────────────────────────────────

    [Fact]
    public void MultiArgPrint_IsWarning()
    {
        const string script = "print('User:', configured_user)";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue("multi-arg print() produces a Warning, not a violation");
        report.Violations.Should().BeEmpty();
        report.Warnings.Should().Contain(w => w.Construct.Contains("print"));
    }

    [Fact]
    public void SingleArgPrint_IsNotWarned()
    {
        // print('single string') in Python 2/Jython is fine — it's just calling a function.
        const string script = "print('[WLST-DIAG] Admin MBean discovery start')";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue();
        report.Warnings.Should().NotContain(w => w.Construct.Contains("print"),
            "single-argument print() is unambiguous in both Python 2 and 3");
    }

    // ── Multiple violations accumulate ────────────────────────────────────────

    [Fact]
    public void MultipleViolations_AllReported()
    {
        const string script = """
            x = a if cond else b
                except Exception as e:
                    pass
            """;
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeFalse();
        report.Violations.Should().HaveCountGreaterThanOrEqualTo(2,
            "both 'except ... as ...' and the ternary expression must be reported independently");
    }

    // ── Empty / null edge cases ───────────────────────────────────────────────

    [Fact]
    public void EmptyScript_IsCompatible()
    {
        var report = WlstJythonCompatibilityValidator.Validate(string.Empty);

        report.IsCompatible.Should().BeTrue("an empty script has no incompatibilities");
        report.Violations.Should().BeEmpty();
        report.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void NullScript_ThrowsArgumentNullException()
    {
        var act = () => WlstJythonCompatibilityValidator.Validate(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Report summary text ───────────────────────────────────────────────────

    [Fact]
    public void Report_Summary_Compatible_MentionsWarningCount()
    {
        const string script = "print('[WLST-DIAG] msg', extra)";  // multi-arg print → 1 warning
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeTrue();
        report.Summary.Should().Contain("1 warning",
            "the compatible summary must include the number of warnings");
    }

    [Fact]
    public void Report_Summary_Incompatible_MentionsViolationCount()
    {
        const string script = "    except Exception as e:";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.IsCompatible.Should().BeFalse();
        report.Summary.Should().Contain("JYTHON SYNTAX ERROR",
            "incompatible summary must call out JYTHON SYNTAX ERROR prominently");
        report.Summary.Should().Contain("1 violation",
            "summary must state the violation count so the error is actionable");
    }

    // ── JythonViolation.ToString() format ────────────────────────────────────

    [Fact]
    public void ViolationToString_ContainsLineNumber_And_Severity()
    {
        const string script = "    except Exception as e:";
        var report = WlstJythonCompatibilityValidator.Validate(script);

        report.Violations.Should().HaveCount(1);
        var text = report.Violations[0].ToString();

        text.Should().Contain("[Line 1]",   "ToString must include the 1-based line number");
        text.Should().Contain("SyntaxError", "ToString must include the severity");
    }

    // ── Clean Jython 2.2 script passes completely ─────────────────────────────

    [Fact]
    public void CleanJython22Script_IsCompatible_NoViolationsNoWarnings()
    {
        // Representative WLST helper block — same pattern as WlstDomainScriptHelpers emits.
        const string cleanScript = """
            def _wedm_ls():
                try:
                    raw = ls()
                    if raw is None:
                        return []
                    result = []
                    for i in raw:
                        s = str(i).strip()
                        if s:
                            result.append(s)
                    return result
                except:
                    return []

            def _wedm_discover_admin_path(configured_user):
                print('[WLST-DIAG] Admin MBean discovery start')
                cd('/')
                _root = _wedm_ls()
                try:
                    cd('/Security')
                except Exception, _e:
                    raise Exception('[WLST-DIAG] cd(/Security) failed: ' + str(_e))
                _realms = _wedm_ls()
                _realm = _realms[0]
                try:
                    cd(_realm + '/User')
                except Exception, _e:
                    raise Exception('[WLST-DIAG] cd failed: ' + str(_e))
                _users = _wedm_ls()
                if configured_user in _users:
                    _user = configured_user
                else:
                    _user = _users[0]
                _path = '/Security/' + _realm + '/User/' + _user
                cd('/')
                return _path
            """;

        var report = WlstJythonCompatibilityValidator.Validate(cleanScript);

        report.IsCompatible.Should().BeTrue(
            "the Jython 2.2-compliant helper block must pass with zero violations");
        report.Violations.Should().BeEmpty();
    }
}

// ── 2. Regression tests: generated scripts must be Jython 2.2-clean ──────────

public sealed class WlstJythonRegressionTests
{
    // ── Config helpers ────────────────────────────────────────────────────────

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

    private static DeploymentConfiguration Make11gConfig() => new()
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
            DomainName      = "wls11g_domain",
            AdminServerName = "AdminServer",
            AdminUsername   = "weblogic",
            AdminPassword   = "Welcome11g",
            AdminPort       = 7001,
        },
        Network         = new NetworkConfiguration { Hostname = "localhost" },
        DomainHardening = new DomainHardeningConfiguration { ProductionMode = false },
    };

    // ── The primary regression: no "except X as e:" in any generated script ───

    /// <summary>
    /// THE SMOKING-GUN REGRESSION TEST.
    ///
    /// Before the fix, WlstDomainScriptHelpers emitted:
    ///   except Exception as _e:
    ///
    /// Jython 2.2 (WLS 11g) parses the entire script before executing any of it.
    /// A single "except X as e:" anywhere causes:
    ///   Errors: Problem invoking WLST - Traceback (innermost last):
    ///     (no code object) at line 0
    ///     SyntaxError: invalid syntax
    ///
    /// The fix: always emit "except Exception, _e:" (Python 2 / Jython 2.x form).
    /// </summary>
    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_ContainsNoExceptAsClause(WebLogicVersion version)
    {
        var config   = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        // Direct string check — catches the exact pattern Jython 2.2 rejects.
        ctx.ScriptContent.Should().NotContain(" as _e:",
            $"'except X as _e:' in the generated {version} script causes a top-level " +
            "SyntaxError in Jython 2.2 (WLS 11g) before any WLST command executes. " +
            "The fix is 'except X, _e:' — the Python 2 / Jython 2.x form.");

        ctx.ScriptContent.Should().NotContain(" as e:",
            $"Any 'except ... as e:' form in the {version} script will fail on Jython 2.2.");

        // More general check via the validator itself
        var nonCommentLines = ctx.ScriptContent
            .Split(new char[] { '\r', '\n' })
            .Where(l => !l.TrimStart().StartsWith('#'))
            .ToList();

        var hasExceptAs = nonCommentLines.Any(l =>
            l.Contains("except ", StringComparison.Ordinal) &&
            l.Contains(" as ",    StringComparison.Ordinal));

        hasExceptAs.Should().BeFalse(
            $"No non-comment line in the generated {version} script may contain 'except ... as ...'");
    }

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_ExceptClausesUseCommaForm(WebLogicVersion version)
    {
        // Positive assertion: the Jython 2.2-compatible form must be present.
        var config   = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().Contain("except Exception, _e:",
            $"The generated {version} script must use 'except Exception, _e:' " +
            "(Python 2 / Jython 2.x form) — NOT the Python 3 'except Exception as _e:' form");
    }

    // ── No ternary expressions ────────────────────────────────────────────────

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_ContainsNoTernaryExpression(WebLogicVersion version)
    {
        // Ternary "x if cond else y" requires Python 2.5+ — SyntaxError in Jython 2.2.
        // The fix is an explicit if/else block.
        var config   = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        // Check non-comment lines only.
        var nonCommentLines = ctx.ScriptContent
            .Split(new char[] { '\r', '\n' })
            .Where(l => !l.TrimStart().StartsWith('#'))
            .ToList();

        // A ternary is " if " followed by " else " on the SAME non-statement line.
        // Statement "if" starts at the beginning of a trimmed line — those are OK.
        var hasTernary = nonCommentLines.Any(l =>
        {
            var trimmed = l.TrimStart();
            if (trimmed.StartsWith("if ", StringComparison.Ordinal))  return false;
            if (trimmed.StartsWith("elif ", StringComparison.Ordinal)) return false;

            var ifIdx   = l.IndexOf(" if ", StringComparison.Ordinal);
            if (ifIdx < 0) return false;
            var elseIdx = l.IndexOf(" else ", ifIdx, StringComparison.Ordinal);
            return elseIdx >= 0;
        });

        hasTernary.Should().BeFalse(
            $"No ternary expression ('x if cond else y') must appear in the generated {version} script. " +
            "Jython 2.2 (WLS 11g) treats ternary syntax as a SyntaxError. " +
            "Use explicit if/else blocks instead.");
    }

    // ── No f-strings ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_ContainsNoFStrings(WebLogicVersion version)
    {
        var config   = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().NotContain("f'",
            $"f-strings (f'...') are Python 3.6+ and not supported by any Jython version. " +
            $"The generated {version} script must use string concatenation instead.");
        ctx.ScriptContent.Should().NotContain("f\"",
            $"f-strings (f\"...\") must not appear in the generated {version} script.");
    }

    // ── No walrus operator ────────────────────────────────────────────────────

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_ContainsNoWalrusOperator(WebLogicVersion version)
    {
        var config   = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().NotContain(":=",
            $"The walrus operator ':=' is Python 3.8+ and is never supported by Jython. " +
            $"The generated {version} script must not contain ':='.");
    }

    // ── Full Jython validator pass on all generated scripts ───────────────────

    /// <summary>
    /// End-to-end: every generated script passes WlstJythonCompatibilityValidator
    /// with zero syntax violations.  This is the gating check that DomainLifecycleSteps
    /// runs as pre-flight step 4b before wlst.cmd is launched.
    /// </summary>
    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_PassesJythonCompatibilityValidator(WebLogicVersion version)
    {
        var config   = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        var jythonReport = WlstJythonCompatibilityValidator.Validate(ctx.ScriptContent);

        jythonReport.IsCompatible.Should().BeTrue(
            $"The generated {version} WLST script must pass the Jython pre-flight validator " +
            $"with zero syntax violations. Violations found:\n" +
            string.Join("\n  ", jythonReport.Violations.Select(v => v.ToString())));

        jythonReport.Violations.Should().BeEmpty(
            $"Zero SyntaxError-class violations must exist in the {version} script. " +
            "Any violation would cause wlst.cmd to fail at parse time with '(no code object) at line 0'.");
    }

    // ── Explicit if/else for user selection (ternary replacement) ─────────────

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_UsesExplicitIfElseForUserSelection(WebLogicVersion version)
    {
        // Before the fix, the discovery helper used a ternary:
        //   _user = configured_user if configured_user in _users else _users[0]
        // After the fix, it uses an explicit if/else block:
        //   if configured_user in _users:
        //       _user = configured_user
        //   else:
        //       _user = _users[0]
        var config   = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().Contain("if configured_user in _users:",
            $"The {version} script must use an explicit 'if configured_user in _users:' block " +
            "— not a ternary expression — to remain Jython 2.2-compatible.");

        ctx.ScriptContent.Should().Contain("_user = configured_user",
            "The true-branch of the explicit if must assign _user = configured_user");

        ctx.ScriptContent.Should().Contain("_user = _users[0]",
            "The else-branch must fall back to _users[0]");
    }

    // ── Explicit for loop instead of list comprehension / filter ──────────────

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    public void GeneratedScript_UsesExplicitForLoopInLsHelper(WebLogicVersion version)
    {
        // Jython 2.2 list comprehensions can be fragile with Java array types.
        // The _wedm_ls() helper uses an explicit for loop to populate the result list.
        var config   = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().Contain("for i in raw:",
            $"_wedm_ls() in the {version} script must iterate raw with an explicit for loop, " +
            "not a list comprehension, for maximum Jython 2.2 compatibility.");

        ctx.ScriptContent.Should().Contain("result.append(s)",
            "Items must be appended via explicit append() call in the for loop.");
    }

    // ── Diagnostic print statements use string concatenation (not f-strings) ──

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_DiagPrints_UseStringConcatenation(WebLogicVersion version)
    {
        var config   = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        // Check that diagnostic prints use ' + str(var) pattern, not f-strings.
        ctx.ScriptContent.Should().Contain("str(_e)",
            $"Exception messages in the {version} script must use str() for conversion, " +
            "not f-strings or %-formatting.");

        ctx.ScriptContent.Should().NotContain("f'[WLST-DIAG]",
            "Diagnostic print statements must use string concatenation, not f-strings.");
        ctx.ScriptContent.Should().NotContain("f\"[WLST-DIAG]",
            "Diagnostic print statements must use string concatenation, not f-string doubles.");
    }

    // ── WLST API validator also passes (belt-and-suspenders) ─────────────────

    [Theory]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void GeneratedScript_PassesBothValidators_12cFamily(WebLogicVersion version)
    {
        var config   = Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        var apiReport    = WlstCompatibilityValidator.Validate(ctx.ScriptContent, version);
        var jythonReport = WlstJythonCompatibilityValidator.Validate(ctx.ScriptContent);

        apiReport.IsCompatible.Should().BeTrue(
            $"Generated {version} script must pass the API compatibility validator " +
            $"(no set('Password'), cmo.setPassword present, readTemplate/writeDomain/exit present). " +
            $"Violations: {string.Join(", ", apiReport.Violations)}");

        jythonReport.IsCompatible.Should().BeTrue(
            $"Generated {version} script must pass the Jython syntax validator. " +
            $"Violations: {string.Join(", ", jythonReport.Violations.Select(v => v.ToString()))}");
    }

    [Fact]
    public void GeneratedScript_PassesBothValidators_11g()
    {
        var config   = Make11gConfig();
        var provider = WlstDomainScriptProviderFactory.Create(WebLogicVersion.WLS_11g);
        var ctx      = provider.BuildCreateDomainScript(config);

        var apiReport    = WlstCompatibilityValidator.Validate(ctx.ScriptContent, WebLogicVersion.WLS_11g);
        var jythonReport = WlstJythonCompatibilityValidator.Validate(ctx.ScriptContent);

        apiReport.IsCompatible.Should().BeTrue(
            "Generated 11g script must pass the API compatibility validator. " +
            $"Violations: {string.Join(", ", apiReport.Violations)}");

        jythonReport.IsCompatible.Should().BeTrue(
            "Generated 11g script must pass the Jython syntax validator. " +
            $"Violations: {string.Join(", ", jythonReport.Violations.Select(v => v.ToString()))}");
    }
}
