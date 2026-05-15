using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation;

/// <summary>Facade for transformation preview; full pipeline via <see cref="ITransformationOrchestrator"/>.</summary>
public sealed class ConfigurationTransformationEngine : IConfigurationTransformationEngine
{
    private readonly ITransformationOrchestrator _orchestrator;

    public ConfigurationTransformationEngine(ITransformationOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public async Task<string> BuildTransformationPlanPreviewAsync(
        MigrationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.TransformationCompleted && !string.IsNullOrWhiteSpace(configuration.Transformation?.PlanPreview))
            return configuration.Transformation.PlanPreview;

        var result = await _orchestrator.ExecuteAsync(configuration, cancellationToken: cancellationToken);
        configuration.Transformation              = result;
        configuration.TransformationCompleted     = result.Completed;
        configuration.TransformationDurationMs    = result.TotalDurationMs;
        configuration.TransformationWorkspacePath = result.WorkspacePath;
        configuration.FormsModernization          = result.FormsModernization;
        configuration.ReportsModernization        = result.ReportsModernization;
        return result.PlanPreview;
    }
}
