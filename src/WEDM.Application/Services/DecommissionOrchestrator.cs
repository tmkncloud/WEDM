using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Decommissioning;

namespace WEDM.Application.Services;

/// <summary>Application-layer entry point for Remove WebLogic Environment workflows.</summary>
public sealed class DecommissionOrchestrator
{
    private readonly MiddlewareRemovalOrchestrator _removal;
    private readonly ILoggingService _log;

    public DecommissionOrchestrator(MiddlewareRemovalOrchestrator removal, ILoggingService log)
    {
        _removal = removal;
        _log     = log;
    }

    public event EventHandler<double>? OverallProgressChanged;

    public async Task<DecommissionReport> ExecuteDecommissionAsync(
        DecommissionConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.Options.ConfirmationPhrase))
            throw new InvalidOperationException("Confirmation phrase is required for decommission.");

        _log.Info($"Decommission session starting: {config.Name}", "DecommissionOrchestrator");
        OverallProgressChanged?.Invoke(this, 5);

        var report = await _removal.ExecuteAsync(config, cancellationToken).ConfigureAwait(false);

        OverallProgressChanged?.Invoke(this, 100);
        _log.Info($"Decommission session ended: {report.FinalStatus}", "DecommissionOrchestrator");
        return report;
    }
}
