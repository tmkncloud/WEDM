using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>Placeholder until Forms/Reports silent pipeline is wired to OUI response files.</summary>
public sealed class InstallFormsReportsStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public InstallFormsReportsStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        _log.Warning("Forms/Reports install step deferred — configure ResponseFileGenerator + media paths before production use.", "Forms");
        return Task.FromResult(StepExecutionResult.Ok("Forms/Reports install skipped in this build."));
    }
}

/// <summary>Placeholder until OHS WebTier silent install is wired.</summary>
public sealed class InstallOhsWebTierStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public InstallOhsWebTierStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        _log.Warning("OHS/WebTier install step deferred.", "OHS");
        return Task.FromResult(StepExecutionResult.Ok("OHS/WebTier install skipped in this build."));
    }
}
