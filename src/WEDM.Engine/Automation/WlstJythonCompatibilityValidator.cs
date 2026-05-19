namespace WEDM.Engine.Automation;

/// <summary>
/// Scans a generated WLST Python script for constructs that are syntactically invalid
/// under Jython 2.2+ before the script is handed to wlst.cmd.
///
/// Why this exists
/// ───────────────
/// WLST embeds Jython rather than CPython.  The embedded Jython version varies by
/// WebLogic release:
///
///   WLS 10.3.x (11g)  → Jython 2.2.1
///   WLS 12.1.x/12.2.x → Jython 2.5.3
///   WLS 14.1.x        → Jython 2.7.x
///
/// Jython parses the ENTIRE script before executing any of it.  A single Python 3
/// syntax construct anywhere in the file causes an immediate top-level SyntaxError:
///
///   Errors: Problem invoking WLST - Traceback (innermost last):
///     (no code object) at line 0
///     File "wedm_create_domain_...py",
///           except Exception as _e:
///                            ^
///     SyntaxError: invalid syntax
///
/// This validator catches those constructs at WEDM generation time — before WLST
/// starts its JVM — so the failure message is clear and actionable.
///
/// Constructs detected
/// ───────────────────
/// VIOLATION (script will fail to parse on any Jython version):
///   • "except Exception as "    → use "except Exception, " (Py2 form)
///   • "except <Type> as "       → same rule for any exception type
///
/// VIOLATION (script will fail to parse on Jython 2.2):
///   • Ternary expression "x if <cond> else y"  (requires Python 2.5+)
///
/// WARNING (cosmetic / forward-compatibility):
///   • f-string literals  f'...' / f"..."       → use string concatenation
///   • Walrus operator :=                        → not supported in any Jython
///   • print(a, b, c) multi-argument form        → ambiguous in Py2 (tuple print)
///   • Type annotations  "def foo(x: int)"       → ignored at runtime but signals Py3 drift
/// </summary>
public static class WlstJythonCompatibilityValidator
{
    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="script"/> and returns a report listing every Jython
    /// incompatibility found.  An empty Violations list means the script is safe to
    /// pass to wlst.cmd.
    /// </summary>
    public static WlstJythonCompatibilityReport Validate(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var violations = new List<JythonViolation>();
        var warnings   = new List<JythonViolation>();
        var lines      = script.Split(new char[] { '\r', '\n' });

        for (var i = 0; i < lines.Length; i++)
        {
            var raw     = lines[i];
            var trimmed = raw.TrimStart();
            var lineNo  = i + 1;

            // Skip comment lines — no syntax issues there.
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;

            CheckExceptAsClause(raw, lineNo, violations);
            CheckTernaryExpression(raw, lineNo, violations);
            CheckFStrings(raw, lineNo, warnings);
            CheckWalrusOperator(raw, lineNo, warnings);
            CheckMultiArgPrint(raw, lineNo, warnings);
        }

        return new WlstJythonCompatibilityReport
        {
            IsCompatible = violations.Count == 0,
            Violations   = violations.AsReadOnly(),
            Warnings     = warnings.AsReadOnly(),
        };
    }

    // ── Check methods ─────────────────────────────────────────────────────────

    /// <summary>
    /// Detects "except SomeType as varname:" — the Python 3 / Python 2.6+ form.
    /// Jython 2.2 and 2.5 require "except SomeType, varname:" instead.
    ///
    /// Pattern matched: "except" followed (after optional whitespace and an identifier)
    /// by the keyword " as " followed by a valid Python identifier.
    /// </summary>
    private static void CheckExceptAsClause(string line, int lineNo, List<JythonViolation> violations)
    {
        // Quick scan: "as " must appear somewhere after "except"
        var exceptIdx = line.IndexOf("except", StringComparison.Ordinal);
        if (exceptIdx < 0) return;

        // Find " as " after the "except" keyword (with surrounding spaces to avoid
        // matching identifiers like "basis", "class", etc.)
        var asIdx = line.IndexOf(" as ", exceptIdx, StringComparison.Ordinal);
        if (asIdx < 0) return;

        // Confirm the token before "as" is not inside a string literal —
        // a simple heuristic: count unescaped quotes before asIdx.
        // If the number of unescaped ' and " is even, we're not inside a string.
        if (IsInsideStringLiteral(line, asIdx)) return;

        violations.Add(new JythonViolation
        {
            LineNumber = lineNo,
            LineText   = line.Trim(),
            Construct  = "except … as …",
            Message    = "'except X as e:' is not valid in Jython 2.2/2.5. "
                       + "Replace with 'except X, e:' (Python 2 / Jython 2.x form).",
            Severity   = JythonViolationSeverity.SyntaxError,
        });
    }

    /// <summary>
    /// Detects Python 2.5+ ternary expressions: "value_a if condition else value_b".
    /// These are NOT supported by Jython 2.2 (WLS 11g) and cause a SyntaxError.
    /// </summary>
    private static void CheckTernaryExpression(string line, int lineNo, List<JythonViolation> violations)
    {
        // Look for " if " surrounded by other tokens on the same line,
        // where " else " also appears after the " if ".
        // Heuristic: both " if " and " else " appear as separate tokens on the same line,
        // but not as standalone statement keywords (i.e. not starting the trimmed line).
        var trimmed = line.TrimStart();

        // Standalone "if" at start of a trimmed line → this is a normal if-statement, not ternary.
        // We're looking for " if " appearing INSIDE an expression.
        if (trimmed.StartsWith("if ", StringComparison.Ordinal)
         || trimmed.StartsWith("elif ", StringComparison.Ordinal))
            return;

        var ifIdx = line.IndexOf(" if ", StringComparison.Ordinal);
        if (ifIdx < 0) return;

        var elseIdx = line.IndexOf(" else ", ifIdx, StringComparison.Ordinal);
        if (elseIdx < 0) return;

        if (IsInsideStringLiteral(line, ifIdx)) return;

        violations.Add(new JythonViolation
        {
            LineNumber = lineNo,
            LineText   = line.Trim(),
            Construct  = "ternary expression (x if cond else y)",
            Message    = "Ternary 'x if cond else y' requires Python 2.5+ and is not valid "
                       + "in Jython 2.2 (WLS 11g). Use an explicit if/else block instead.",
            Severity   = JythonViolationSeverity.SyntaxError,
        });
    }

    /// <summary>Detects f-string literals: f'...' or f"..."</summary>
    private static void CheckFStrings(string line, int lineNo, List<JythonViolation> warnings)
    {
        if (line.Contains("f'", StringComparison.Ordinal)
         || line.Contains("f\"", StringComparison.Ordinal))
        {
            warnings.Add(new JythonViolation
            {
                LineNumber = lineNo,
                LineText   = line.Trim(),
                Construct  = "f-string",
                Message    = "f-strings (f'...' / f\"...\") are Python 3.6+ and not supported "
                           + "by any Jython version. Use string concatenation: 'text ' + str(var).",
                Severity   = JythonViolationSeverity.Warning,
            });
        }
    }

    /// <summary>Detects walrus operator := which is Python 3.8+ and never supported by Jython.</summary>
    private static void CheckWalrusOperator(string line, int lineNo, List<JythonViolation> warnings)
    {
        if (line.Contains(":=", StringComparison.Ordinal))
        {
            warnings.Add(new JythonViolation
            {
                LineNumber = lineNo,
                LineText   = line.Trim(),
                Construct  = "walrus operator :=",
                Message    = "The walrus operator ':=' is Python 3.8+ and is never supported by Jython.",
                Severity   = JythonViolationSeverity.Warning,
            });
        }
    }

    /// <summary>
    /// Detects multi-argument print() calls: print(a, b) — in Python 2 / Jython this prints
    /// a tuple rather than two separate values, which is almost certainly unintended.
    /// </summary>
    private static void CheckMultiArgPrint(string line, int lineNo, List<JythonViolation> warnings)
    {
        var idx = line.IndexOf("print(", StringComparison.Ordinal);
        if (idx < 0) return;

        // Very rough check: look for a comma inside the print() parentheses
        // that isn't inside a string literal.
        var open = line.IndexOf('(', idx + 5);
        if (open < 0) return;

        var depth  = 0;
        var inStr  = false;
        var strCh  = ' ';
        var commas = 0;

        for (var j = open; j < line.Length; j++)
        {
            var ch = line[j];
            if (inStr)
            {
                if (ch == strCh && (j == 0 || line[j - 1] != '\\')) inStr = false;
                continue;
            }
            if (ch is '\'' or '"') { inStr = true; strCh = ch; continue; }
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth--; if (depth == 0) break; continue; }
            if (ch == ',' && depth == 1) commas++;
        }

        if (commas > 0)
        {
            warnings.Add(new JythonViolation
            {
                LineNumber = lineNo,
                LineText   = line.Trim(),
                Construct  = "print() with multiple arguments",
                Message    = "print(a, b) in Python 2/Jython prints a tuple. "
                           + "Use string concatenation: print(a + ' ' + b).",
                Severity   = JythonViolationSeverity.Warning,
            });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Rough check: returns true if the character at <paramref name="index"/> appears
    /// inside a string literal (by counting unescaped quote characters before it).
    /// Only single-line strings are considered; multiline strings are not handled.
    /// </summary>
    private static bool IsInsideStringLiteral(string line, int index)
    {
        var singleCount = 0;
        var doubleCount = 0;

        for (var i = 0; i < index && i < line.Length; i++)
        {
            if (line[i] == '\'' && (i == 0 || line[i - 1] != '\\')) singleCount++;
            if (line[i] == '"'  && (i == 0 || line[i - 1] != '\\')) doubleCount++;
        }

        // Inside a single-quoted string if odd single-quote count and no unclosed double-quote
        return (singleCount % 2 == 1) || (doubleCount % 2 == 1);
    }
}

// ── Report types ──────────────────────────────────────────────────────────────

/// <summary>Result of a <see cref="WlstJythonCompatibilityValidator.Validate"/> call.</summary>
public sealed class WlstJythonCompatibilityReport
{
    /// <summary><c>true</c> when no syntax violations were found (warnings may still exist).</summary>
    public bool IsCompatible                           { get; init; }

    /// <summary>Hard syntax errors that will cause Jython to refuse to parse the script.</summary>
    public IReadOnlyList<JythonViolation> Violations   { get; init; } = [];

    /// <summary>Soft warnings: constructs that are technically valid but risky or version-dependent.</summary>
    public IReadOnlyList<JythonViolation> Warnings     { get; init; } = [];

    public string Summary => IsCompatible
        ? $"Jython-compatible — {Warnings.Count} warning(s)."
        : $"JYTHON SYNTAX ERROR — {Violations.Count} violation(s), {Warnings.Count} warning(s). "
        + "Script will fail to parse before any WLST commands execute.";
}

/// <summary>A single Jython compatibility finding.</summary>
public sealed class JythonViolation
{
    public int                    LineNumber { get; init; }
    public string                 LineText   { get; init; } = string.Empty;
    public string                 Construct  { get; init; } = string.Empty;
    public string                 Message    { get; init; } = string.Empty;
    public JythonViolationSeverity Severity  { get; init; }

    public override string ToString()
        => $"[Line {LineNumber}] {Severity}: {Construct} — {LineText}";
}

public enum JythonViolationSeverity { SyntaxError, Warning }
