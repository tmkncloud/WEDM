namespace WEDM.Domain.Models;

/// <summary>
/// Structured record of a rollback session produced by <c>DeploymentWorkflowEngine.RollbackAsync</c>.
/// Replaces the previous bare <see cref="bool"/> return value, providing operator-grade transparency
/// about exactly which steps were reversed, which had no registered executor, and which failed.
/// </summary>
public sealed class RollbackSummary
{
    public DateTimeOffset  StartedAt       { get; init; }  = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt     { get; set;  }
    public TimeSpan?       Duration        => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;

    /// <summary>
    /// True only when every eligible step was reversed by its executor with no failures and no missing executors.
    /// A partial rollback (some steps had no executor or failed) leaves this false.
    /// </summary>
    public bool FullyRolledBack     { get; set; }

    /// <summary>Count of steps that were successfully reversed by their rollback executors.</summary>
    public int  StepsRolledBack     { get; set; }

    /// <summary>
    /// Count of steps eligible for rollback but with no executor registered for their <c>RollbackAction</c>.
    /// These steps remain on disk and require manual operator intervention.
    /// </summary>
    public int  StepsNoExecutor     { get; set; }

    /// <summary>Count of steps where the rollback executor returned failure or threw an exception.</summary>
    public int  StepsFailed         { get; set; }

    /// <summary>
    /// Executor reported success but the operation still requires explicit operator action
    /// (RCU drops, VC++ uninstall, third-party installers). Not counted as automated reversal.
    /// </summary>
    public int StepsManualInterventionRequired { get; set; }

    /// <summary>Ordered records, one per eligible step (reverse execution order).</summary>
    public List<RollbackStepRecord> Records { get; init; } = new();

    /// <summary>
    /// Oracle-specific rollback narrative accumulated by Oracle rollback executors during the rollback pass.
    /// Populated by <c>DeploymentWorkflowEngine</c> after the full rollback pass completes by copying
    /// <c>DeploymentConfiguration.OracleRollback</c>.
    /// Null when no Oracle rollback executors ran (e.g. rollback of non-Oracle steps only).
    /// </summary>
    public OracleRollbackReport? OracleDetails { get; set; }
}

/// <summary>Individual rollback outcome for one deployment step.</summary>
public sealed class RollbackStepRecord
{
    /// <summary>Original execution sequence number of the step being reversed.</summary>
    public int     Sequence        { get; init; }
    public string  StepName        { get; init; } = string.Empty;
    public string  RollbackAction  { get; init; } = string.Empty;

    /// <summary>
    /// One of: <c>RolledBack</c>, <c>ManualInterventionRequired</c>, <c>NoExecutor</c>, <c>Failed</c>, <c>Exception</c>.
    /// </summary>
    public string  Outcome         { get; set;  } = string.Empty;
    public bool    Success         { get; set;  }
    public string  Output          { get; set;  } = string.Empty;
    public string  Error           { get; set;  } = string.Empty;
    public TimeSpan Duration       { get; set;  }
}
