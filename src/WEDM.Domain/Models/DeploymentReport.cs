using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

/// <summary>
/// Complete deployment report generated at the end of a deployment session.
/// Persisted to JSON and rendered as an HTML report for auditing and compliance.
/// </summary>
public sealed class DeploymentReport
{
    public Guid                 ReportId          { get; init; } = Guid.NewGuid();
    public string               DeploymentName    { get; set; } = string.Empty;
    public Guid                 ConfigurationId   { get; set; }
    public string               Environment       { get; set; } = string.Empty;
    public string               MachineName       { get; set; } = System.Environment.MachineName;
    public string               ExecutedBy        { get; set; } = System.Environment.UserName;
    public DateTimeOffset       StartedAt         { get; set; }
    public DateTimeOffset?      CompletedAt       { get; set; }
    public TimeSpan?            TotalDuration     => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;
    public DeploymentStatus     FinalStatus       { get; set; } = DeploymentStatus.NotStarted;
    public WebLogicVersion      Version           { get; set; }
    public string               Platform          { get; set; } = "Windows Server";
    public string               OsVersion         { get; set; } = string.Empty;
    public string               InstalledJdkPath  { get; set; } = string.Empty;
    public string               MiddlewareHome    { get; set; } = string.Empty;
    public string               DomainHome        { get; set; } = string.Empty;
    public string               AdminUrl          { get; set; } = string.Empty;

    public List<DeploymentStep>     Steps            { get; set; } = new();
    public List<LogEntry>           AuditLog         { get; set; } = new();
    public PrerequisiteValidationResult? Validation  { get; set; }

    /// <summary>
    /// Populated when a rollback was triggered. Null if the deployment succeeded or rollback was disabled.
    /// Exposes per-step rollback outcomes for operator-grade audit and HTML reporting.
    /// </summary>
    public RollbackSummary?         Rollback         { get; set; }

    public int TotalSteps     => Steps.Count;
    public int StepsSucceeded => Steps.Count(s => s.Status == StepStatus.Succeeded);
    public int StepsFailed    => Steps.Count(s => s.Status == StepStatus.Failed);
    public int StepsSkipped   => Steps.Count(s => s.Status == StepStatus.Skipped);
    public double SuccessRate => TotalSteps > 0 ? (double)StepsSucceeded / TotalSteps * 100 : 0;
}

/// <summary>
/// Structured log entry for audit trail.
/// </summary>
public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public LogLevel       Level     { get; init; } = LogLevel.Info;
    public string         Category  { get; init; } = string.Empty;
    public string         Message   { get; init; } = string.Empty;
    public string?        StepName  { get; init; }
    public string?        Details   { get; init; }
    public Exception?     Exception { get; init; }
}
