using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Persists OPatch compliance artefacts (HTML + JSON).</summary>
public interface IPatchReportWriter
{
    Task WriteHtmlAsync(PatchReport report, string outputPath, CancellationToken cancellationToken = default);

    Task WriteJsonAsync(PatchReport report, string outputPath, CancellationToken cancellationToken = default);
}
