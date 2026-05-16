using WEDM.Engine.Jdk.Strategies;

namespace WEDM.Engine.Jdk;

public sealed class JdkInstallerStrategyFactory
{
    private readonly IReadOnlyList<IJdkInstallerStrategy> _strategies;

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

    public JdkInstallerStrategyFactory(IEnumerable<IJdkInstallerStrategy> strategies)
        => _strategies = strategies.ToList();

    public IJdkInstallerStrategy Resolve(string installerPath)
    {
        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(installerPath));
        if (strategy is null)
            throw new NotSupportedException($"No JDK installer strategy for: {installerPath}");
        return strategy;
    }
}
