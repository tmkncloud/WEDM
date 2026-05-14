using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

/// <summary>
/// Result of a single validation check executed by the PrerequisiteValidator.
/// </summary>
public sealed record ValidationFinding(
    string            CheckName,
    ValidationSeverity Severity,
    bool              Passed,
    string            Message,
    string?           Remediation = null,
    object?           ActualValue = null,
    object?           ExpectedValue = null
);

/// <summary>
/// Aggregated result of all prerequisite validation checks for a deployment.
/// </summary>
public sealed class PrerequisiteValidationResult
{
    private readonly List<ValidationFinding> _findings = new();

    public IReadOnlyList<ValidationFinding> Findings     => _findings.AsReadOnly();
    public IReadOnlyList<ValidationFinding> Errors       => _findings.Where(f => f.Severity >= ValidationSeverity.Error && !f.Passed).ToList();
    public IReadOnlyList<ValidationFinding> Warnings     => _findings.Where(f => f.Severity == ValidationSeverity.Warning && !f.Passed).ToList();
    public IReadOnlyList<ValidationFinding> PassedChecks => _findings.Where(f => f.Passed).ToList();

    public DateTimeOffset ValidatedAt { get; } = DateTimeOffset.UtcNow;
    public string MachineName         { get; } = Environment.MachineName;
    public string ValidatedBy         { get; } = Environment.UserName;

    /// <summary>
    /// True only when no Fatal or Error-level findings exist.
    /// </summary>
    public bool CanProceed => !_findings.Any(f => !f.Passed && f.Severity >= ValidationSeverity.Error);

    /// <summary>
    /// True when all checks passed without any findings.
    /// </summary>
    public bool IsClean => _findings.All(f => f.Passed);

    public void Add(ValidationFinding finding) => _findings.Add(finding);

    public void AddRange(IEnumerable<ValidationFinding> findings) => _findings.AddRange(findings);

    public ValidationFinding Pass(string checkName, string message, object? actual = null)
    {
        var f = new ValidationFinding(checkName, ValidationSeverity.Info, true, message, null, actual);
        _findings.Add(f);
        return f;
    }

    public ValidationFinding Warn(string checkName, string message, string? remediation = null, object? actual = null, object? expected = null)
    {
        var f = new ValidationFinding(checkName, ValidationSeverity.Warning, false, message, remediation, actual, expected);
        _findings.Add(f);
        return f;
    }

    public ValidationFinding Fail(string checkName, string message, string? remediation = null, object? actual = null, object? expected = null)
    {
        var f = new ValidationFinding(checkName, ValidationSeverity.Error, false, message, remediation, actual, expected);
        _findings.Add(f);
        return f;
    }

    public ValidationFinding Fatal(string checkName, string message, string? remediation = null)
    {
        var f = new ValidationFinding(checkName, ValidationSeverity.Fatal, false, message, remediation);
        _findings.Add(f);
        return f;
    }

    public int TotalChecks   => _findings.Count;
    public int PassedCount   => _findings.Count(f => f.Passed);
    public int FailedCount   => _findings.Count(f => !f.Passed && f.Severity >= ValidationSeverity.Error);
    public int WarningCount  => _findings.Count(f => !f.Passed && f.Severity == ValidationSeverity.Warning);
    public int FatalCount    => _findings.Count(f => !f.Passed && f.Severity == ValidationSeverity.Fatal);
    public double PassRate   => TotalChecks > 0 ? (double)PassedCount / TotalChecks * 100.0 : 0;

    // Aliases used by DeploymentOrchestrator
    public int PassCount  => PassedCount;
    public int WarnCount  => WarningCount;
    public int ErrorCount => FailedCount;
    public int Fatals     => FatalCount;

    /// <summary>Optional deployment correlation ID.</summary>
    public Guid DeploymentId { get; private set; }

    /// <summary>Merge all findings from another result set into this one.</summary>
    public void Merge(PrerequisiteValidationResult other)
        => _findings.AddRange(other._findings);

    /// <summary>Factory — create an empty result associated with a deployment.</summary>
    public static PrerequisiteValidationResult New(Guid deploymentId)
        => new() { DeploymentId = deploymentId };
}
