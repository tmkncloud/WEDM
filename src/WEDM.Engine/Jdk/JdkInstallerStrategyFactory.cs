using Microsoft.Extensions.DependencyInjection;
using WEDM.Engine.Jdk.Strategies;

namespace WEDM.Engine.Jdk;

public sealed class JdkInstallerStrategyFactory
{
    private readonly IReadOnlyList<IJdkInstallerStrategy> _strategies;

    /// <summary>Default production strategies (Oracle EXE, Temurin MSI, generic MSI/EXE).</summary>
    [ActivatorUtilitiesConstructor]
    public JdkInstallerStrategyFactory()
    {
        _strategies =
        [
            new OracleJdk8ExeInstallerStrategy(),
            new TemurinMsiInstallerStrategy(),
            new MsiJdkInstallerStrategy(),
            new GenericExeJdkInstallerStrategy()
        ];
    }

    /// <summary>Test-only: supply a custom strategy list.</summary>
    internal JdkInstallerStrategyFactory(IReadOnlyList<IJdkInstallerStrategy> strategies)
        => _strategies = strategies;

    public IJdkInstallerStrategy Resolve(string installerPath)
    {
        if (_strategies.Count == 0)
            throw new InvalidOperationException("JDK installer strategy list is empty — DI misconfiguration.");

        var normalized = JdkInstallerPathNormalizer.Normalize(installerPath);
        var strategy   = _strategies.FirstOrDefault(s => s.CanHandle(normalized));
        if (strategy is null)
            throw new NotSupportedException($"No JDK installer strategy for: {normalized}");
        return strategy;
    }
}
