using System.Xml.Linq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation;

public sealed class TransformationValidationEngine : ITransformationValidationEngine
{
    public TransformationValidationSummary Validate(
        MigrationConfiguration configuration,
        TransformationExecutionResult result)
    {
        var messages = new List<TransformationValidationMessage>();

        if (!configuration.DiscoveryCompleted)
            messages.Add(Msg(TransformationValidationSeverity.Blocker, "Discovery must complete before transformation validation."));

        if (!configuration.AssessmentCompleted)
            messages.Add(Msg(TransformationValidationSeverity.Warning, "Compatibility assessment not completed — plan confidence reduced."));

        foreach (var artifact in result.Artifacts.Where(a => a.Kind == TransformationArtifactKind.WlstScript))
        {
            var path = Path.Combine(result.WorkspacePath, artifact.RelativePath);
            if (!File.Exists(path))
            {
                messages.Add(Msg(TransformationValidationSeverity.Error, $"Missing WLST artifact: {artifact.RelativePath}", path));
                continue;
            }

            var content = File.ReadAllText(path);
            if (content.Contains("***CHANGE_PASSWORD***", StringComparison.Ordinal))
                messages.Add(Msg(TransformationValidationSeverity.Warning, "WLST scripts contain placeholder credentials — replace before execution.", path));

            if (!content.Contains("exit()", StringComparison.Ordinal))
                messages.Add(Msg(TransformationValidationSeverity.Warning, "WLST script missing exit() — review script structure.", path));
        }

        foreach (var ct in result.ConfigTransformations)
        {
            if (ct.OutputPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ct.TransformedExcerpt))
            {
                try
                {
                    XDocument.Parse(ct.TransformedExcerpt);
                }
                catch
                {
                    if (ct.TransformedExcerpt.TrimStart().StartsWith('<'))
                        messages.Add(Msg(TransformationValidationSeverity.Warning, $"Transformed XML may be invalid: {ct.OutputPath}"));
                }
            }
        }

        if (result.Artifacts.Count == 0)
            messages.Add(Msg(TransformationValidationSeverity.Error, "No transformation artifacts were generated."));

        var blockers = messages.Count(m => m.Severity == TransformationValidationSeverity.Blocker);
        var warnings = messages.Count(m => m.Severity is TransformationValidationSeverity.Warning or TransformationValidationSeverity.Error);

        var confidence = blockers > 0
            ? TransformationConfidenceKind.Low
            : warnings > 3
                ? TransformationConfidenceKind.Moderate
                : TransformationConfidenceKind.High;

        return new TransformationValidationSummary
        {
            Passed         = blockers == 0,
            Confidence     = confidence,
            BlockerCount   = blockers,
            WarningCount   = warnings,
            Messages       = messages,
        };
    }

    private static TransformationValidationMessage Msg(
        TransformationValidationSeverity severity, string message, string? path = null) => new()
    {
        Severity     = severity,
        Message      = message,
        ArtifactPath = path,
    };
}
