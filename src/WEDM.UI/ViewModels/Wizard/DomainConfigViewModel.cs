using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.UI.Services;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Step 5: Domain topology and server configuration.
/// User defines the WebLogic domain, AdminServer, managed servers, and Node Manager.
/// </summary>
public sealed partial class DomainConfigViewModel : WizardStepViewModel
{
    // ── Domain identity ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _domainName = "wls_domain";

    [ObservableProperty]
    private DomainTopology _topology = DomainTopology.Standard;

    // ── Admin Server ───────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _adminServerName = "AdminServer";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private int _adminPort = 7001;

    [ObservableProperty]
    private int _adminSslPort = 7002;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _adminUsername = "weblogic";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _adminPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _confirmPassword = string.Empty;

    [ObservableProperty] private string _domainNameError = string.Empty;
    [ObservableProperty] private string _adminServerNameError = string.Empty;
    [ObservableProperty] private string _adminPortError = string.Empty;
    [ObservableProperty] private string _adminUsernameError = string.Empty;
    [ObservableProperty] private string _adminPasswordError = string.Empty;
    [ObservableProperty] private string _confirmPasswordError = string.Empty;

    // ── Node Manager ──────────────────────────────────────────────────────────

    [ObservableProperty] private int    _nodeManagerPort    = 5556;
    [ObservableProperty] private string _nodeManagerType    = "Plain";
    [ObservableProperty] private bool   _registerNmService  = true;

    // ── Managed Servers ────────────────────────────────────────────────────────

    public ObservableCollection<ManagedServerRowViewModel> ManagedServers { get; } = new()
    {
        new ManagedServerRowViewModel { Name = "WLS_FORMS",   Port = 9001, SslPort = 9002 },
        new ManagedServerRowViewModel { Name = "WLS_REPORTS", Port = 9003, SslPort = 9004 },
    };

    [RelayCommand]
    private void AddManagedServer()
    {
        int nextPort = ManagedServers.Count > 0
            ? ManagedServers.Max(s => s.Port) + 2
            : 9001;
        ManagedServers.Add(new ManagedServerRowViewModel
        {
            Name    = $"WLS_MS{ManagedServers.Count + 1}",
            Port    = nextPort,
            SslPort = nextPort + 1
        });
        RefreshTopologyNodes();
    }

    [RelayCommand]
    private void RemoveManagedServer(ManagedServerRowViewModel row)
    {
        ManagedServers.Remove(row);
        RefreshTopologyNodes();
    }

    // ── Computed ──────────────────────────────────────────────────────────────

    public bool PasswordsMatch => AdminPassword == ConfirmPassword;

    public string PasswordMatchMessage => PasswordsMatch
        ? string.Empty
        : "Passwords do not match.";

    public IReadOnlyList<DomainTopology> Topologies { get; } = Enum.GetValues<DomainTopology>();
    public IReadOnlyList<string>        NmTypes         { get; } = ["Plain", "SSL"];

    [ObservableProperty] private string _profileSummary = string.Empty;

    [ObservableProperty] private string _nodeManagerStatusHint =
        "Online automation validates Node Manager TCP when enabled; strict PROD profiles fail if NM is not listening.";

    public ObservableCollection<TopologyNodeVm> TopologyNodes { get; } = new();

    public override Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        DomainName        = config.Domain.DomainName;
        Topology          = config.Domain.Topology;
        AdminServerName   = config.Domain.AdminServerName;
        AdminPort         = config.Domain.AdminPort;
        AdminSslPort      = config.Domain.AdminSslPort;
        AdminUsername     = config.Domain.AdminUsername;
        AdminPassword     = config.Domain.AdminPassword;
        ConfirmPassword     = config.Domain.AdminPassword;
        NodeManagerPort   = config.Domain.NodeManager.Port;
        NodeManagerType   = config.Domain.NodeManager.Type;
        RegisterNmService = config.Domain.NodeManager.RegisterService;

        ManagedServers.Clear();
        foreach (var m in config.Domain.ManagedServers)
        {
            ManagedServers.Add(new ManagedServerRowViewModel
            {
                Name            = m.Name,
                Port            = m.Port,
                SslPort         = m.SslPort,
                RegisterService = m.RegisterService
            });
        }

        ProfileSummary =
            $"Environment: {config.DeploymentEnvironment} — offline production mode: {(config.DomainHardening.ProductionMode ? "yes" : "no")}; " +
            $"online WLST / nmEnroll: {(config.DomainOnlineAutomation.Enabled ? "on" : "off")}; " +
            $"Node Manager: {config.Domain.NodeManager.Type} on port {config.Domain.NodeManager.Port}";

        RefreshTopologyNodes();
        ValidateFields();
        return Task.CompletedTask;
    }

    private void RefreshTopologyNodes()
    {
        TopologyNodes.Clear();
        TopologyNodes.Add(new TopologyNodeVm("Admin", AdminServerName, AdminPort, "●"));
        foreach (var ms in ManagedServers)
            TopologyNodes.Add(new TopologyNodeVm("Managed", ms.Name, ms.Port, "○"));
    }

    partial void OnAdminServerNameChanged(string value)
    {
        RefreshTopologyNodes();
        ValidateFields();
    }

    partial void OnAdminPortChanged(int value)
    {
        RefreshTopologyNodes();
        ValidateFields();
    }

    partial void OnDomainNameChanged(string value) => ValidateFields();
    partial void OnAdminUsernameChanged(string value) => ValidateFields();

    public override bool CanProceed =>
        string.IsNullOrEmpty(DomainNameError) &&
        string.IsNullOrEmpty(AdminServerNameError) &&
        string.IsNullOrEmpty(AdminPortError) &&
        string.IsNullOrEmpty(AdminUsernameError) &&
        string.IsNullOrEmpty(AdminPasswordError) &&
        string.IsNullOrEmpty(ConfirmPasswordError) &&
        !IsBusy;

    protected override void RunStepValidation() => ValidateFields();

    private void ValidateFields()
    {
        DomainNameError = WizardValidationHelper.IsValidIdentifier(DomainName, "Domain name", out var dn)
            ? string.Empty : dn;
        AdminServerNameError = WizardValidationHelper.IsValidIdentifier(AdminServerName, "Admin server name", out var sn)
            ? string.Empty : sn;
        AdminPortError = WizardValidationHelper.IsValidPort(AdminPort, out var pe) ? string.Empty : pe;
        AdminUsernameError = WizardValidationHelper.IsRequiredText(AdminUsername, "Administrator user", out var ue)
            ? string.Empty : ue;
        AdminPasswordError = WizardValidationHelper.IsRequiredText(AdminPassword, "Password", out var pw)
            ? string.Empty : pw;
        ConfirmPasswordError = PasswordsMatch
            ? string.Empty
            : "Passwords do not match.";

        IsValid = CanProceed;
        OnPropertyChanged(nameof(CanProceed));
        OnPropertyChanged(nameof(HasStepValidationError));
    }

    public DomainConfigViewModel()
    {
        StepTitle       = "Domain";
        StepDescription = "Configure the WebLogic domain topology and server definitions.";
        StepIcon        = "🌐";
        ManagedServers.CollectionChanged += (_, _) => RefreshTopologyNodes();
    }

    partial void OnAdminPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(PasswordsMatch));
        OnPropertyChanged(nameof(PasswordMatchMessage));
        ValidateFields();
    }

    partial void OnConfirmPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(PasswordsMatch));
        OnPropertyChanged(nameof(PasswordMatchMessage));
        ValidateFields();
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        config.Domain.DomainName      = DomainName;
        config.Domain.Topology        = Topology;
        config.Domain.AdminServerName = AdminServerName;
        config.Domain.AdminPort       = AdminPort;
        config.Domain.AdminSslPort    = AdminSslPort;
        config.Domain.AdminUsername   = AdminUsername;
        config.Domain.AdminPassword   = AdminPassword;  // encrypted by engine

        config.Domain.NodeManager.Port            = NodeManagerPort;
        config.Domain.NodeManager.Type            = NodeManagerType;
        config.Domain.NodeManager.RegisterService = RegisterNmService;

        config.Domain.ManagedServers = ManagedServers.Select(row => new ManagedServerDefinition
        {
            Name    = row.Name,
            Port    = row.Port,
            SslPort = row.SslPort,
            Type    = ServerType.ManagedServer,
            RegisterService = row.RegisterService
        }).ToList();
    }
}

/// <summary>Row view-model for a single managed server in the domain config grid.</summary>
public sealed partial class ManagedServerRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name    = string.Empty;
    [ObservableProperty] private int    _port    = 9001;
    [ObservableProperty] private int    _sslPort = 9002;
    [ObservableProperty] private bool   _registerService = true;
}

/// <summary>Lightweight row for topology visualization cards.</summary>
public sealed class TopologyNodeVm
{
    public string Role         { get; }
    public string Name        { get; }
    public int    Port         { get; }
    public string StatusGlyph { get; }

    public TopologyNodeVm(string role, string name, int port, string statusGlyph)
    {
        Role         = role;
        Name         = name;
        Port         = port;
        StatusGlyph  = statusGlyph;
    }
}
