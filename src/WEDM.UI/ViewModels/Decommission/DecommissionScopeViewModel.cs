using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Decommission;

public sealed partial class DecommissionScopeViewModel : DecommissionWizardStepViewModel
{
    [ObservableProperty] private string _oracleRoot = @"D:\Oracle";
    [ObservableProperty] private string _middlewareHome = @"D:\Oracle\Oracle_MW";
    [ObservableProperty] private string _domainBase = @"D:\Oracle\Oracle_MW\user_projects\domains";
    [ObservableProperty] private string _oracleInventory = @"D:\Oracle\oraInventory";
    [ObservableProperty] private string _reportsDirectory = @"D:\Oracle\WEDM\reports";

    public DecommissionScopeViewModel()
    {
        StepTitle       = "Decommission Scope";
        StepDescription = "Specify Oracle paths to remove. WEDM will discover all related assets before cleanup.";
        StepIcon        = "🎯";
    }

    public override bool CanProceed =>
        !string.IsNullOrWhiteSpace(MiddlewareHome) && !string.IsNullOrWhiteSpace(OracleInventory);

    public override void ApplyToDecommissionConfiguration(DecommissionConfiguration config)
    {
        config.Paths.OracleRoot       = OracleRoot;
        config.Paths.MiddlewareHome   = MiddlewareHome;
        config.Paths.DomainBase       = DomainBase;
        config.Paths.OracleInventory  = OracleInventory;
        config.Paths.ReportsDirectory = ReportsDirectory;
        config.Paths.TempDirectory    = Path.Combine(OracleRoot, "Temp");
        config.Paths.LogDirectory     = Path.Combine(OracleRoot, "WEDM", "logs");
        config.Paths.SnapshotDirectory = Path.Combine(OracleRoot, "WEDM", "snapshots");
        config.Name = $"Decommission — {Path.GetFileName(MiddlewareHome.TrimEnd('\\'))}";
    }

    partial void OnOracleRootChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        MiddlewareHome   = Path.Combine(value, "Oracle_MW");
        DomainBase       = Path.Combine(value, "Oracle_MW", "user_projects", "domains");
        OracleInventory  = Path.Combine(value, "oraInventory");
        ReportsDirectory = Path.Combine(value, "WEDM", "reports");
    }
}
