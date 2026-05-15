using WEDM.Domain.Models;

namespace WEDM.Engine.Transformation.Modernization;

internal static class ReportsModernizationAnalyzer
{
    public static ReportsModernizationSnapshot Analyze(MigrationConfiguration config)
    {
        var snapshot = new ReportsModernizationSnapshot
        {
            ReportInventoryCount = config.FormsMetadata.ReportCount,
        };

        foreach (var server in config.Topology.ReportsServers)
            snapshot.ServerDependencies.Add($"{server.Name} @ {server.Url ?? "unknown"}");

        if (config.Topology.ReportsServers.Count == 0 && config.FormsMetadata.ReportCount > 0)
            snapshot.ServerDependencies.Add("Reports server topology not fully discovered — validate rwserver instances");

        snapshot.OutputFormats.AddRange(["PDF", "HTML", "XML", "DELIMITED"]);
        snapshot.CustomRuntimeDetected = config.FormsMetadata.UsesOracleGraphics;

        if (config.FormsMetadata.UsesOracleGraphics)
        {
            snapshot.UnsupportedFeatures.Add("Oracle Graphics charting in Reports");
            snapshot.MigrationCandidates.Add("Replace Graphics charts with BI Publisher or RDF-native charts");
        }

        if (config.Source.Release is Domain.Enums.MiddlewareReleaseKind.Forms6i or Domain.Enums.MiddlewareReleaseKind.Forms10g)
            snapshot.UnsupportedFeatures.Add("Legacy Reports character mode runtime");

        snapshot.ReadinessSummary = snapshot.UnsupportedFeatures.Count == 0
            ? "Reports modernization preparation indicates standard uplift path"
            : $"{snapshot.UnsupportedFeatures.Count} unsupported feature area(s) require remediation";

        if (config.FormsMetadata.ReportCount > 50)
            snapshot.MigrationCandidates.Add("Phased Reports module migration by business domain");

        return snapshot;
    }
}
