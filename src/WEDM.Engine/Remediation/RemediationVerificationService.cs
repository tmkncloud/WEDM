using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Remediation;

public sealed class RemediationVerificationService : IRemediationVerificationService
{
    private readonly IOracleInventoryService _inventory;

    public RemediationVerificationService(IOracleInventoryService inventory) => _inventory = inventory;

    public RemediationVerificationResult Verify(DeploymentConfiguration config)
    {
        var mw  = config.Paths.MiddlewareHome;
        var inv = config.Paths.OracleInventory;
        var validation = _inventory.ValidateForInstall(mw, inv);
        var homeState  = _inventory.DetectHomeState(mw, inv);

        var findings = new List<string>(validation.Findings);
        if (!Directory.Exists(mw))
            findings.Add("Verification: middleware home directory removed or absent ✔");

        var passed = validation.CanProceed
                     && homeState is OracleHomeState.Clean or OracleHomeState.Unknown;

        return new RemediationVerificationResult
        {
            Passed                 = passed,
            HomeStateAfter         = homeState,
            CanProceedWithInstall  = validation.CanProceed,
            Findings               = findings,
        };
    }
}
