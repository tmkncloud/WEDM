using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

public sealed class InventoryPointerContext
{
    public string CentralInventoryRoot { get; init; } = string.Empty;
    public string PointerFilePath { get; init; } = string.Empty;
    public InventoryPointerScope Scope { get; init; } = InventoryPointerScope.DefaultCentral;
    public bool IsIsolated { get; init; }
}

public sealed class InventoryBootstrapPlan
{
    public string InventoryRoot { get; init; } = string.Empty;
    public string InventoryXmlPath { get; init; } = string.Empty;
    public IReadOnlyList<string> DirectoriesToCreate { get; init; } = [];
    public IReadOnlyList<string> FilesToWrite { get; init; } = [];
    public string VersionProfile { get; init; } = string.Empty;
    public BootstrapVersionStrategy Strategy { get; init; }
    public bool CanExecute { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class InventoryBootstrapSafetyResult
{
    public bool IsSafe { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<string> BlockingReasons { get; init; } = [];
}

public sealed class InventoryBootstrapAssessment
{
    public OracleCentralInventoryState State { get; init; } = OracleCentralInventoryState.Missing;
    public bool RequiresBootstrap { get; init; }
    public bool CanAutoBootstrap { get; init; }
    public InventoryBootstrapSafetyResult Safety { get; init; } = new();
    public InventoryBootstrapPlan? Plan { get; init; }
}

public sealed class InventoryBootstrapValidationResult
{
    public bool Passed { get; init; }
    public OracleCentralInventoryState ResultingState { get; init; } = OracleCentralInventoryState.Missing;
    public IReadOnlyList<string> Findings { get; init; } = [];
}

public sealed class OracleInventoryBootstrapReport
{
    public Guid ReportId { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool DryRun { get; init; }
    public bool Success { get; set; }
    public BootstrapVersionStrategy Strategy { get; init; }
    public string VersionProfile { get; init; } = string.Empty;
    public string InventoryRoot { get; init; } = string.Empty;
    public IReadOnlyList<string> CreatedDirectories { get; init; } = [];
    public IReadOnlyList<string> WrittenFiles { get; init; } = [];
    public InventoryPointerContext? PointerContext { get; init; }
    public InventoryBootstrapValidationResult? Validation { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

public sealed class OracleInventoryBootstrapResult
{
    public bool Success { get; init; }
    public bool ContinuationRecommended { get; init; }
    public OracleInventoryBootstrapReport Report { get; init; } = new();
}

public sealed class InventoryBootstrapExecutionOptions
{
    public bool DryRun { get; init; }
    public string Trigger { get; init; } = string.Empty;
    public InventoryPointerScope PointerScope { get; init; } = InventoryPointerScope.DefaultCentral;
}

public sealed class InventoryBootstrapCheckpoint
{
    [JsonPropertyName("deploymentId")]
    public Guid DeploymentId { get; init; }

    [JsonPropertyName("completedSteps")]
    public List<string> CompletedSteps { get; set; } = [];

    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
}
