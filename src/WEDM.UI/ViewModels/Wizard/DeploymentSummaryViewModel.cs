using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Security;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Step 7: Deployment summary / review screen.
/// Presents a read-only review of all collected configuration before the user
/// initiates the actual deployment. Also allows exporting the configuration to JSON.
/// </summary>
public sealed partial class DeploymentSummaryViewModel : WizardStepViewModel
{
    private DeploymentConfiguration? _config;

    // ── Computed summary sections ──────────────────────────────────────────────

    [ObservableProperty] private string _versionSummary   = string.Empty;
    [ObservableProperty] private string _pathSummary      = string.Empty;
    [ObservableProperty] private string _databaseSummary  = string.Empty;
    [ObservableProperty] private string _domainSummary    = string.Empty;
    [ObservableProperty] private string _componentSummary = string.Empty;
    [ObservableProperty] private string _securityHardeningSummary = string.Empty;
    [ObservableProperty] private string _configJson       = string.Empty;

    [ObservableProperty] private bool   _showJson;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private bool _termsAccepted;

    public override bool CanProceed => TermsAccepted;

    public DeploymentSummaryViewModel()
    {
        StepTitle       = "Summary";
        StepDescription = "Review your deployment configuration before starting.";
        StepIcon        = "📋";
    }

    public override Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        _config = config;
        PopulateSummary(config);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ToggleJson() => ShowJson = !ShowJson;

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        if (_config is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "Export Configuration",
            Filter           = "JSON files (*.json)|*.json",
            FileName         = $"wedm-config-{_config.Id:N}.json",
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() != true) return;

        await File.WriteAllTextAsync(dlg.FileName, DeploymentConfigurationSanitizer.ToSafeJson(_config));
    }

    private void PopulateSummary(DeploymentConfiguration config)
    {
        var patchMode = !config.Patches.Enabled
            ? "OPatch: disabled (install software only)"
            : config.Patches.StandalonePatchWorkflow
                ? "OPatch: standalone patch workflow (not used with full wizard deploy)"
                : $"OPatch: enabled after install — staging: {config.Patches.PatchStagingDirectory}";

        VersionSummary = $"""
            Version           : {config.WebLogicVersion}
            Environment label : {config.Environment}
            Deployment profile: {config.DeploymentEnvironment}
            Components        : {config.Components}
            {patchMode}
            """;

        PathSummary = $"""
            Middleware Home: {config.Paths.MiddlewareHome}
            Domain Base    : {config.Paths.DomainBase}
            Oracle Root    : {config.Paths.OracleRoot}
            Log Directory  : {config.Paths.LogDirectory}
            """;

        DomainSummary = $"""
            Domain    : {config.Domain.DomainName}
            Topology  : {config.Domain.Topology}
            AdminServer: {config.Domain.AdminServerName} (port {config.Domain.AdminPort})
            Managed Servers: {config.Domain.ManagedServers.Count}
            Node Manager  : port {config.Domain.NodeManager.Port} ({config.Domain.NodeManager.Type})
            """;

        DatabaseSummary = config.Database.RunRcu
            ? $"""
              RCU    : Enabled
              DB Host: {config.Database.Host}:{config.Database.Port}/{config.Database.ServiceName}
              Schema : {config.Database.SchemaPrefix}_*
              """
            : "RCU    : Disabled (no database schema creation)";

        var flags = config.Components.ToString().Replace(", ", "\n           ");
        ComponentSummary = $"Components: {flags}";

        SecurityHardeningSummary = $"""
            Deployment profile  : {config.DeploymentEnvironment} ({config.Environment})
            Production domain   : {(config.DomainHardening.ProductionMode ? "yes" : "no")}
            Online WLST / nmEnroll: {(config.DomainOnlineAutomation.Enabled ? "enabled" : "disabled")}
            Admin SSL port       : {config.Domain.AdminSslPort}
            Node Manager         : {config.Domain.NodeManager.Type} / port {config.Domain.NodeManager.Port}
            Encrypted passwords  : {(config.Security.UseEncryptedPasswords ? "yes" : "no")}
            Strict post-validation: {(config.DomainHardening.StrictPostValidation ? "yes" : "no")}
            """;

        ConfigJson = DeploymentConfigurationSanitizer.ToSafeJson(config);
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        // Summary is read-only — no mutations
    }
}
