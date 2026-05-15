using System.Diagnostics;
using System.Text.Json;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Migration;
using WEDM.Domain.Models;
using WEDM.Engine.Transformation.Modernization;
using WEDM.Engine.Transformation.Transformers;
using WEDM.Engine.Transformation.Wlst;
using WEDM.Infrastructure.Migration;

namespace WEDM.Engine.Transformation;

public sealed class TransformationOrchestrator : ITransformationOrchestrator
{
    private readonly MigrationWorkspaceManager _workspace = new();
    private readonly ITransformationValidationEngine _validation;
    private readonly IMigrationPlanGenerator _planGenerator;

    public event EventHandler<TransformationProgressEventArgs>? ProgressChanged;

    public TransformationOrchestrator(
        ITransformationValidationEngine validation,
        IMigrationPlanGenerator planGenerator)
    {
        _validation    = validation;
        _planGenerator = planGenerator;
    }

    public async Task<TransformationExecutionResult> ExecuteAsync(
        MigrationConfiguration configuration,
        TransformationExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TransformationExecutionOptions();
        var sw = Stopwatch.StartNew();
        var result = new TransformationExecutionResult();
        var stages = new List<TransformationStageResult>();
        var artifacts = new List<GeneratedTransformationArtifact>();
        var warnings = new List<string>();

        try
        {
            // Stage 1: Workspace
            string workspace = string.Empty;
            await RunStageAsync(TransformationStageKind.WorkspaceInitialization, "Initialize migration workspace",
                stages, 0.05, cancellationToken, async () =>
                {
                    workspace = _workspace.CreateWorkspace(configuration, options.WorkspaceRoot);
                    result.WorkspacePath = workspace;
                    await _workspace.WriteDiscoverySnapshotAsync(workspace, configuration, cancellationToken);
                    return true;
                });

            // Stage 2-3: Modernization analysis
            await RunStageAsync(TransformationStageKind.FormsModernizationAnalysis, "Forms modernization analysis",
                stages, 0.15, cancellationToken, () =>
                {
                    result.FormsModernization = FormsModernizationAnalyzer.Analyze(configuration);
                    return Task.FromResult(true);
                });

            await RunStageAsync(TransformationStageKind.ReportsModernizationAnalysis, "Reports modernization analysis",
                stages, 0.22, cancellationToken, () =>
                {
                    result.ReportsModernization = ReportsModernizationAnalyzer.Analyze(configuration);
                    return Task.FromResult(true);
                });

            // Stage 4-9: Config transformations
            var configTransforms = new List<ConfigTransformationRecord>();
            await RunStageAsync(TransformationStageKind.DomainTransformation, "Domain configuration preparation",
                stages, 0.32, cancellationToken, () =>
                {
                    AddTransform(configTransforms, DomainConfigPrepTransformer.Transform(configuration));
                    return Task.FromResult(true);
                });

            await RunStageAsync(TransformationStageKind.JvmModernization, "JVM startup modernization",
                stages, 0.40, cancellationToken, () =>
                {
                    AddTransform(configTransforms, JvmStartupTransformer.Transform(workspace, configuration));
                    return Task.FromResult(true);
                });

            await RunStageAsync(TransformationStageKind.NodeManagerTransformation, "Node Manager modernization",
                stages, 0.48, cancellationToken, () =>
                {
                    AddTransform(configTransforms, NodeManagerConfigTransformer.Transform(configuration));
                    return Task.FromResult(true);
                });

            await RunStageAsync(TransformationStageKind.SslHardeningPreparation, "SSL/TLS hardening preparation",
                stages, 0.52, cancellationToken, () => Task.FromResult(true));

            await RunStageAsync(TransformationStageKind.FormsConfigTransformation, "Forms configuration transformation",
                stages, 0.58, cancellationToken, () =>
                {
                    AddTransform(configTransforms, FormsConfigTransformer.Transform(configuration));
                    return Task.FromResult(true);
                });

            await RunStageAsync(TransformationStageKind.ReportsConfigTransformation, "Reports configuration transformation",
                stages, 0.64, cancellationToken, () =>
                {
                    AddTransform(configTransforms, ReportsConfigTransformer.Transform(configuration));
                    return Task.FromResult(true);
                });

            foreach (var ct in configTransforms)
            {
                var content = ct.TransformedExcerpt ?? ct.Summary;
                await TransformationSafeIO.WriteWorkspaceFileAsync(workspace, ct.OutputPath, content, cancellationToken);
                artifacts.Add(new GeneratedTransformationArtifact
                {
                    Kind         = TransformationArtifactKind.TransformedConfig,
                    RelativePath = ct.OutputPath,
                    DisplayName  = Path.GetFileName(ct.OutputPath),
                    Description  = ct.Summary,
                });

                var comparisonPath = ct.OutputPath.Replace(Path.GetExtension(ct.OutputPath), ".comparison.md");
                var comparison = $"# {ct.Summary}\n\n## Source\n`{ct.SourcePath}`\n\n## Original\n```\n{ct.OriginalExcerpt}\n```\n\n## Transformed\n```\n{ct.TransformedExcerpt}\n```\n";
                await TransformationSafeIO.WriteWorkspaceFileAsync(workspace, comparisonPath, comparison, cancellationToken);
                artifacts.Add(new GeneratedTransformationArtifact
                {
                    Kind         = TransformationArtifactKind.ComparisonReport,
                    RelativePath = comparisonPath,
                    DisplayName  = Path.GetFileName(comparisonPath),
                });
            }

            result.ConfigTransformations = configTransforms;

            // Stage 10: WLST
            await RunStageAsync(TransformationStageKind.WlstGeneration, "WLST script generation",
                stages, 0.78, cancellationToken, async () =>
                {
                    var targetDomain = options.TargetDomainHome
                        ?? Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                            "WEDM", "target-domains",
                            configuration.Topology.DomainName ?? "migration_domain");

                    var ctx = WlstMigrationScriptBuilder.BuildContext(configuration, targetDomain);
                    foreach (var (fileName, script) in WlstMigrationScriptBuilder.BuildAll(configuration, ctx))
                    {
                        var rel = $"{MigrationWorkspaceManager.WlstDir}/{fileName}";
                        await TransformationSafeIO.WriteWorkspaceFileAsync(workspace, rel, script, cancellationToken);
                        var full = Path.Combine(workspace, rel);
                        artifacts.Add(new GeneratedTransformationArtifact
                        {
                            Kind         = TransformationArtifactKind.WlstScript,
                            RelativePath = rel,
                            DisplayName  = fileName,
                            Description  = "Review before manual execution",
                            SizeBytes    = new FileInfo(full).Length,
                        });
                    }
                    return true;
                });

            // Stage 11: Remediation
            await RunStageAsync(TransformationStageKind.RemediationGeneration, "Remediation recommendations",
                stages, 0.86, cancellationToken, async () =>
                {
                    result.Remediations = RemediationRecommendationBuilder.Build(configuration, result);
                    var json = JsonSerializer.Serialize(result.Remediations, MigrationJsonOptions.Create());
                    var rel  = $"{MigrationWorkspaceManager.RemediationDir}/remediation-recommendations.json";
                    await TransformationSafeIO.WriteWorkspaceFileAsync(workspace, rel, json, cancellationToken);
                    artifacts.Add(new GeneratedTransformationArtifact
                    {
                        Kind         = TransformationArtifactKind.RemediationReport,
                        RelativePath = rel,
                        DisplayName  = "remediation-recommendations.json",
                    });
                    return true;
                });

            // Stage 12: Validation
            await RunStageAsync(TransformationStageKind.ArtifactValidation, "Artifact validation",
                stages, 0.92, cancellationToken, () =>
                {
                    result.Validation = _validation.Validate(configuration, result);
                    return Task.FromResult(true);
                });

            // Stage 13: Migration plan
            await RunStageAsync(TransformationStageKind.MigrationPlanGeneration, "Migration plan generation",
                stages, 0.96, cancellationToken, async () =>
                {
                    result.MigrationPlan = _planGenerator.Generate(configuration, result);
                    var json = JsonSerializer.Serialize(result.MigrationPlan, MigrationJsonOptions.Create());
                    var jsonPath = $"{MigrationWorkspaceManager.PlansDir}/migration-plan.json";
                    await TransformationSafeIO.WriteWorkspaceFileAsync(workspace, jsonPath, json, cancellationToken);
                    var html = BuildPlanHtml(configuration, result.MigrationPlan);
                    var htmlPath = $"{MigrationWorkspaceManager.ReportsDir}/migration-plan.html";
                    await TransformationSafeIO.WriteWorkspaceFileAsync(workspace, htmlPath, html, cancellationToken);
                    artifacts.Add(new GeneratedTransformationArtifact { Kind = TransformationArtifactKind.MigrationPlan, RelativePath = jsonPath, DisplayName = "migration-plan.json" });
                    artifacts.Add(new GeneratedTransformationArtifact { Kind = TransformationArtifactKind.MigrationPlan, RelativePath = htmlPath, DisplayName = "migration-plan.html" });
                    return true;
                });

            // Stage 14: Manifest
            await RunStageAsync(TransformationStageKind.ManifestFinalization, "Finalize workspace manifest",
                stages, 1.0, cancellationToken, async () =>
                {
                    result.Artifacts = artifacts;
                    var validationPath = $"{MigrationWorkspaceManager.ValidationDir}/validation-summary.json";
                    await TransformationSafeIO.WriteWorkspaceFileAsync(workspace, validationPath,
                        JsonSerializer.Serialize(result.Validation, MigrationJsonOptions.Create()), cancellationToken);
                    artifacts.Add(new GeneratedTransformationArtifact
                    {
                        Kind         = TransformationArtifactKind.ValidationSummary,
                        RelativePath = validationPath,
                        DisplayName  = "validation-summary.json",
                    });

                    await _workspace.WriteRollbackNotesAsync(workspace, result.MigrationPlan.RollbackSteps, cancellationToken);
                    artifacts.Add(new GeneratedTransformationArtifact
                    {
                        Kind         = TransformationArtifactKind.RollbackNotes,
                        RelativePath = MigrationWorkspaceManager.RollbackNotesFile,
                        DisplayName  = "rollback-notes.md",
                    });

                    var manifest = new TransformationWorkspaceManifest
                    {
                        SessionId     = configuration.Id,
                        CreatedAtUtc  = DateTimeOffset.UtcNow,
                        SourceRelease = configuration.Source.DisplayName,
                        TargetRelease = configuration.Target.DisplayName,
                        ProjectName   = configuration.Name,
                        ArtifactCount = artifacts.Count,
                        WorkspacePath = workspace,
                    };
                    await _workspace.WriteManifestAsync(workspace, manifest, cancellationToken);
                    artifacts.Add(new GeneratedTransformationArtifact
                    {
                        Kind         = TransformationArtifactKind.Manifest,
                        RelativePath = MigrationWorkspaceManager.ManifestFile,
                        DisplayName  = "manifest.json",
                    });

                    result.Artifacts = artifacts;
                    return true;
                });

            result.Stages     = stages;
            result.Warnings   = warnings;
            result.Confidence = result.Validation.Confidence;
            result.Completed  = result.Validation.Passed;
            result.PlanPreview = BuildPlanPreview(configuration, result);
        }
        catch (OperationCanceledException)
        {
            warnings.Add("Transformation was cancelled.");
            result.Completed = false;
        }
        catch (Exception ex)
        {
            warnings.Add($"Transformation error: {ex.Message}");
            result.Completed = false;
        }

        result.TotalDurationMs = sw.ElapsedMilliseconds;
        result.Warnings = warnings;
        return result;
    }

    private async Task RunStageAsync(
        TransformationStageKind kind,
        string displayName,
        List<TransformationStageResult> stages,
        double percent,
        CancellationToken ct,
        Func<Task<bool>> action)
    {
        var stage = new TransformationStageResult
        {
            Stage       = kind,
            DisplayName = displayName,
            Status      = TransformationStageStatus.Running,
        };
        stages.Add(stage);
        RaiseProgress(stage, percent);

        var sw = Stopwatch.StartNew();
        try
        {
            await action();
            stage.Status     = TransformationStageStatus.Completed;
            stage.Message    = "Completed";
        }
        catch (Exception ex)
        {
            stage.Status  = TransformationStageStatus.Failed;
            stage.Message = ex.Message;
            throw;
        }
        finally
        {
            stage.DurationMs = sw.ElapsedMilliseconds;
            RaiseProgress(stage, percent);
        }
    }

    private void RaiseProgress(TransformationStageResult stage, double percent)
    {
        ProgressChanged?.Invoke(this, new TransformationProgressEventArgs
        {
            Stage          = stage,
            OverallPercent = percent * 100,
        });
    }

    private static void AddTransform(List<ConfigTransformationRecord> list, ConfigTransformationRecord? record)
    {
        if (record is not null) list.Add(record);
    }

    private static string BuildPlanPreview(MigrationConfiguration config, TransformationExecutionResult result)
    {
        var path = MigrationVersionMatrix.DescribeUpgradePath(config.Source.Release, config.Target.Release);
        return $"""
            WEDM Migration Transformation Plan
            ==================================
            Upgrade path: {path}
            Strategy: {config.Strategy}
            Workspace: {result.WorkspacePath}
            Artifacts: {result.Artifacts.Count}
            Confidence: {result.Confidence}
            Remediations: {result.Remediations.Count}

            {result.MigrationPlan.OperatorSummary}
            """;
    }

    private static string BuildPlanHtml(MigrationConfiguration config, MigrationPlanDocument plan)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'><title>Migration Plan</title>");
        sb.Append("<style>body{font-family:Segoe UI,Arial;margin:24px;background:#0d1117;color:#e6edf3}");
        sb.Append("h1,h2{color:#58a6ff}li{margin:6px 0}</style></head><body>");
        sb.Append($"<h1>{E(plan.Title)}</h1><p>{E(plan.UpgradePath)} · {E(plan.Strategy)}</p>");
        sb.Append($"<p>{E(plan.OperatorSummary)}</p><h2>Stages</h2><ol>");
        foreach (var s in plan.Stages)
            sb.Append($"<li><strong>{E(s.Name)}</strong> ({s.EstimatedHours}h) — {E(s.Description)}</li>");
        sb.Append("</ol><h2>Remediation tasks</h2><ul>");
        foreach (var t in plan.RemediationTasks.Take(40))
            sb.Append($"<li>{E(t)}</li>");
        sb.Append("</ul></body></html>");
        return sb.ToString();
    }
}
