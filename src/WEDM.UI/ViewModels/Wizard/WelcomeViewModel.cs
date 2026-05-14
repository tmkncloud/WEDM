using CommunityToolkit.Mvvm.ComponentModel;
using WEDM.Domain.Models;
using WEDM.Engine.Automation;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Step 1: Welcome / overview screen.
/// Displays tool version, feature highlights, and deployment environment profile (DEV–PROD).
/// </summary>
public sealed partial class WelcomeViewModel : WizardStepViewModel
{
    private DeploymentConfiguration? _sessionConfig;
    private bool                     _suppressEnvironmentApply;

    [ObservableProperty]
    private string _appVersion = "1.0.0";

    [ObservableProperty]
    private string _buildDate = DateTime.Today.ToString("yyyy-MM-dd");

    [ObservableProperty]
    private DeploymentEnvironmentKind _selectedDeploymentEnvironment = DeploymentEnvironmentKind.Dev;

    public string WelcomeHeadline    => "WebLogic Enterprise Deployment Manager";
    public string WelcomeSubheadline => "Automated Oracle Middleware Installation for Windows";

    public IReadOnlyList<DeploymentEnvironmentKind> DeploymentEnvironmentOptions { get; }
        = Enum.GetValues<DeploymentEnvironmentKind>().ToArray();

    public IReadOnlyList<string> FeatureHighlights { get; } =
    [
        "Silent installation of Oracle WebLogic 11g, 12c, and 14c",
        "Automated JDK installation and JAVA_HOME configuration",
        "Oracle Forms & Reports + OHS Web Tier provisioning",
        "WLST offline + online automation (nmEnroll, production mode, machine mapping)",
        "DEV / SIT / UAT / PROD environment profiles with hardening presets",
        "RCU schema creation for JRF / Infrastructure installs",
        "Windows service registration with auto-start",
        "OPatch integration with compliance reporting",
        "Structured JSON and HTML deployment reports",
    ];

    public override bool CanProceed => true;

    public WelcomeViewModel()
    {
        StepTitle       = "Welcome";
        StepDescription = "Review the deployment overview before continuing.";
        StepIcon        = "🏠";
    }

    partial void OnSelectedDeploymentEnvironmentChanged(DeploymentEnvironmentKind value)
    {
        if (_suppressEnvironmentApply || _sessionConfig is null) return;
        EnvironmentProfilePresets.Apply(_sessionConfig, value);
    }

    public override Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        _sessionConfig = config;
        _suppressEnvironmentApply = true;
        SelectedDeploymentEnvironment = config.DeploymentEnvironment;
        _suppressEnvironmentApply = false;
        EnvironmentProfilePresets.Apply(config, config.DeploymentEnvironment);
        return Task.CompletedTask;
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        config.DeploymentEnvironment = SelectedDeploymentEnvironment;
    }
}
