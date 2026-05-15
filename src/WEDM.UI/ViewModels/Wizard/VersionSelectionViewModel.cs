using CommunityToolkit.Mvvm.ComponentModel;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Automation;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Step 2: Version and component selection.
/// User selects WebLogic version and which components to install.
/// Drives dynamic display of compatibility notes and component toggles.
/// </summary>
public sealed partial class VersionSelectionViewModel : WizardStepViewModel
{
    // ── Version selection ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedVersionDescription))]
    [NotifyPropertyChangedFor(nameof(RequiredJdk))]
    [NotifyPropertyChangedFor(nameof(MinimumRam))]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private WebLogicVersion _selectedVersion = WebLogicVersion.WLS_12c;

    [ObservableProperty]
    private bool _installInfrastructure = true;

    [ObservableProperty]
    private bool _installFormsReports = true;

    [ObservableProperty]
    private bool _installOhsWebTier = true;

    [ObservableProperty]
    private bool _installJdk = true;

    [ObservableProperty]
    private bool _installVcRedist = true;

    [ObservableProperty]
    private string _environment = "Production";

    // ── Computed display properties ────────────────────────────────────────────

    public string SelectedVersionDescription => SelectedVersion switch
    {
        WebLogicVersion.WLS_11g => "Oracle WebLogic Server 11g Release 1 (10.3.6)\nOracle Forms & Reports 11g R2 (11.1.2.2)\nOracle Web Tier 11g (11.1.1.9)",
        WebLogicVersion.WLS_12c => "Oracle Fusion Middleware Infrastructure 12c (12.2.1.4)\nOracle Forms & Reports 12c (12.2.1.4)\nOracle HTTP Server 12c (12.2.1.4)",
        WebLogicVersion.WLS_14c => "Oracle Fusion Middleware Infrastructure 14c (14.1.2)\nOracle Forms & Reports 14c (14.1.2)\nOracle HTTP Server 14c (14.1.2)",
        _                        => "Unknown version"
    };

    public string RequiredJdk => SelectedVersion switch
    {
        WebLogicVersion.WLS_11g => "JDK 7 or JDK 8 (64-bit)",
        WebLogicVersion.WLS_12c => "JDK 8 Update 202+ (64-bit)",
        WebLogicVersion.WLS_14c => "JDK 21 LTS (64-bit)",
        _                        => "Unknown"
    };

    public string MinimumRam => SelectedVersion switch
    {
        WebLogicVersion.WLS_11g => "4 GB RAM minimum",
        WebLogicVersion.WLS_12c => "8 GB RAM minimum",
        WebLogicVersion.WLS_14c => "8 GB RAM minimum (16 GB recommended)",
        _                        => "Unknown"
    };

    public override bool CanProceed => SelectedVersion != WebLogicVersion.Unknown;

    public IReadOnlyList<string> Environments { get; } =
        ["Development", "SIT", "UAT", "Pre-Production", "Production"];

    public VersionSelectionViewModel()
    {
        StepTitle       = "Version Selection";
        StepDescription = "Choose the Oracle WebLogic version and components to install.";
        StepIcon        = "⚡";
    }

    public override Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        SelectedVersion = config.WebLogicVersion;
        Environment     = string.IsNullOrWhiteSpace(config.Environment)
            ? ToEnvironmentLabel(config.DeploymentEnvironment)
            : config.Environment;
        InstallJdk              = config.Components.HasFlag(InstallationComponents.JDK);
        InstallVcRedist         = config.Components.HasFlag(InstallationComponents.VCRedist);
        InstallInfrastructure   = config.Components.HasFlag(InstallationComponents.Infrastructure);
        InstallFormsReports     = config.Components.HasFlag(InstallationComponents.FormsReports);
        InstallOhsWebTier       = config.Components.HasFlag(InstallationComponents.OHSWebTier);
        return Task.CompletedTask;
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        config.WebLogicVersion = SelectedVersion;
        config.Environment     = Environment;
        EnvironmentProfilePresets.Apply(config, ToDeploymentEnvironmentKind(Environment));

        var components = InstallationComponents.None;
        if (InstallJdk)          components |= InstallationComponents.JDK;
        if (InstallVcRedist)     components |= InstallationComponents.VCRedist;
        if (InstallInfrastructure) components |= InstallationComponents.Infrastructure;
        else                       components |= InstallationComponents.WebLogicServer;
        if (InstallFormsReports) components |= InstallationComponents.FormsReports;
        if (InstallOhsWebTier)   components |= InstallationComponents.OHSWebTier;

        config.Components = components;
        config.ConfigureFormsReports = InstallFormsReports;
    }

    private static DeploymentEnvironmentKind ToDeploymentEnvironmentKind(string env) => env switch
    {
        "Development"    => DeploymentEnvironmentKind.Dev,
        "SIT"            => DeploymentEnvironmentKind.Sit,
        "UAT"            => DeploymentEnvironmentKind.Uat,
        "Pre-Production" => DeploymentEnvironmentKind.Uat,
        "Production"     => DeploymentEnvironmentKind.Prod,
        _                => DeploymentEnvironmentKind.Dev
    };

    private static string ToEnvironmentLabel(DeploymentEnvironmentKind kind) => kind switch
    {
        DeploymentEnvironmentKind.Dev  => "Development",
        DeploymentEnvironmentKind.Sit  => "SIT",
        DeploymentEnvironmentKind.Uat  => "UAT",
        DeploymentEnvironmentKind.Prod => "Production",
        _                              => "Development"
    };
}
