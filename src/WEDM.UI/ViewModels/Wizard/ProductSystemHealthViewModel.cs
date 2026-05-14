using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Registry;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Host runtime, VC++ dependency, packaged media, and optional local update manifest preview.
/// </summary>
public sealed partial class ProductSystemHealthViewModel : WizardStepViewModel
{
    private readonly IProductInfoProvider      _product;
    private readonly IValidationEngine         _validator;
    private readonly WindowsRegistryService    _registry;
    private readonly IUpdateManifestReader    _updateReader;

    private DeploymentConfiguration? _session;

    [ObservableProperty] private string _healthSummary = "Click 'Refresh status' to evaluate this machine and media layout.";

    public ObservableCollection<string> HealthLines { get; } = new();

    public override bool CanProceed => true;

    public ProductSystemHealthViewModel(
        IProductInfoProvider product,
        IValidationEngine validator,
        WindowsRegistryService registry,
        IUpdateManifestReader updateReader)
    {
        _product       = product;
        _validator     = validator;
        _registry      = registry;
        _updateReader  = updateReader;
        StepTitle       = "Product & runtime";
        StepDescription = "Installer health, dependencies, and packaged deployment media.";
        StepIcon        = "📦";
    }

    public override Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        _session = config;
        HealthSummary = "Click 'Refresh status' to evaluate this machine and media layout.";
        return Task.CompletedTask;
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        _ = config;
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        if (_session is null) return;

        SetBusy(true, "Scanning runtime and media…");
        try
        {
            HealthLines.Clear();
            var snap = _product.GetSnapshot();
            AppendLine($"Product: {snap.ProductName}");
            AppendLine($"Display version: {snap.DisplayVersion}");
            AppendLine($"Release channel: {snap.ReleaseChannel}");
            AppendLine($"UI file version: {snap.UiAssemblyFileVersion}");
            AppendLine($"Engine file version: {snap.EngineAssemblyFileVersion}");
            AppendLine($"Runtime: {snap.FrameworkDescription}");

            var vc = _registry.IsVcRedistInstalled();
            AppendLine(vc
                ? "Visual C++ 2015–2022 x64 runtime: detected (registry)."
                : "Visual C++ 2015–2022 x64 runtime: not detected — Oracle client tiers may require it.");

            var payload = await _validator.ValidatePayloadIntegrityAsync(_session, default).ConfigureAwait(true);
            AppendLine("— Packaged media —");
            foreach (var f in payload.Findings)
                AppendLine($"{(f.Passed ? "[OK]" : "[--]")} {f.CheckName}: {f.Message}");

            if (!string.IsNullOrWhiteSpace(snap.UpdateManifestPath) && File.Exists(snap.UpdateManifestPath))
            {
                AppendLine("— Local update manifest —");
                var manifestPath = snap.UpdateManifestPath;
                var um = await _updateReader.TryReadLocalAsync(manifestPath, default).ConfigureAwait(true);
                if (um is null)
                    AppendLine("Update manifest file present but could not be parsed.");
                else
                    AppendLine($"Channel={um.Channel}, availableVersion={um.AvailableVersion}, feed={(string.IsNullOrEmpty(um.PackageFeedUri) ? "(none)" : "configured")}");
            }
            else
                AppendLine("Local update manifest: not configured (expected until an update feed is wired).");

            var sb = new StringBuilder();
            sb.AppendLine(vc ? "VC++ runtime OK." : "VC++ runtime missing or not detected.");
            sb.Append($"Payload checks: {payload.PassedCount} passed, {payload.WarnCount} warnings, {payload.ErrorCount + payload.Fatals} blocking.");
            HealthSummary = sb.ToString().Trim();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AppendLine(string line) => HealthLines.Add(line);
}
