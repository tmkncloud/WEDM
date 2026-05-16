using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Decommission;

public sealed partial class DecommissionDiscoveryViewModel : DecommissionWizardStepViewModel
{
    private readonly IEnvironmentDiscoveryService _discovery;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusMessage = "Run discovery to enumerate Oracle middleware assets.";

    public ObservableCollection<string> DiscoveredItems { get; } = [];

    public DecommissionDiscoveryViewModel(IEnvironmentDiscoveryService discovery)
    {
        _discovery = discovery;
        StepTitle       = "Environment Discovery";
        StepDescription = "Discover middleware homes, domains, services, inventory, processes, and temp artifacts.";
        StepIcon        = "🔍";
    }

    public override bool CanProceed => DiscoveredItems.Count > 0 && !IsScanning;

    public override async Task OnNavigatingToAsync(DecommissionConfiguration config)
    {
        if (config.DiscoveredTopology is not null)
            Populate(config.DiscoveredTopology);
        else
            await RunDiscoveryAsync(config);
    }

    public override void ApplyToDecommissionConfiguration(DecommissionConfiguration config) { }

    private async Task RunDiscoveryAsync(DecommissionConfiguration config)
    {
        IsScanning = true;
        DiscoveredItems.Clear();
        try
        {
            var topo = await _discovery.DiscoverAsync(config);
            config.DiscoveredTopology = topo;
            Populate(topo);
            StatusMessage = $"Discovery complete — {DiscoveredItems.Count} item(s) found.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Discovery failed: {ex.Message}";
            SetError(ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void Populate(EnvironmentTopology topo)
    {
        DiscoveredItems.Clear();
        foreach (var h in topo.MiddlewareHomes)
            DiscoveredItems.Add($"Home: {h.Path}");
        foreach (var d in topo.DomainHomes)
            DiscoveredItems.Add($"Domain: {d}");
        foreach (var s in topo.WindowsServices)
            DiscoveredItems.Add($"Service: {s.ServiceName} ({s.Status})");
        foreach (var p in topo.Processes)
            DiscoveredItems.Add($"Process: {p.ProcessName} PID {p.ProcessId} [{p.Category}]");
        foreach (var w in topo.OrphanWarnings)
            DiscoveredItems.Add($"Warning: {w}");
    }
}
