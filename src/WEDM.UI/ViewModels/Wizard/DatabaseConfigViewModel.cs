using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Net.Sockets;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Step 4: Database / RCU configuration.
/// Collects Oracle DB connection details for Repository Creation Utility execution.
/// Includes a live connectivity test command.
/// </summary>
public sealed partial class DatabaseConfigViewModel : WizardStepViewModel
{
    // ── Connectivity ───────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private bool _runRcu = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _dbHost = "localhost";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private int _dbPort = 1521;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _serviceName = "orcl";

    // ── Credentials ───────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _sysUsername = "system";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _sysPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _schemaPrefix = "DEV";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _schemaPassword = string.Empty;

    // ── NLS ───────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _nlsCharset = "AL32UTF8";

    // ── Connectivity test state ────────────────────────────────────────────────

    [ObservableProperty] private string  _connectivityStatus  = string.Empty;
    [ObservableProperty] private bool    _connectivityPassed;
    [ObservableProperty] private bool    _isTestingConnection;

    public IReadOnlyList<string> NlsCharsets { get; } = ["AL32UTF8", "WE8MSWIN1252", "AR8MSWIN1256", "EE8MSWIN1250"];

    public override bool CanProceed =>
        !RunRcu ||
        (!string.IsNullOrWhiteSpace(DbHost)         &&
         DbPort is > 0 and < 65536                   &&
         !string.IsNullOrWhiteSpace(ServiceName)     &&
         !string.IsNullOrWhiteSpace(SysUsername)     &&
         !string.IsNullOrWhiteSpace(SysPassword)     &&
         !string.IsNullOrWhiteSpace(SchemaPrefix)    &&
         !string.IsNullOrWhiteSpace(SchemaPassword));

    public DatabaseConfigViewModel()
    {
        StepTitle       = "Database";
        StepDescription = "Configure Oracle Database for RCU schema creation.";
        StepIcon        = "🗄️";
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        ConnectivityStatus  = "Testing connection...";
        ConnectivityPassed  = false;

        try
        {
            using var tcp  = new TcpClient();
            var task       = tcp.ConnectAsync(DbHost, DbPort);
            var timeout    = Task.Delay(5000);
            var winner     = await Task.WhenAny(task, timeout);
            ConnectivityPassed = winner == task && tcp.Connected;
            ConnectivityStatus = ConnectivityPassed
                ? $"✔  Connected to {DbHost}:{DbPort} successfully."
                : $"✘  Connection to {DbHost}:{DbPort} timed out or was refused.";
        }
        catch (Exception ex)
        {
            ConnectivityStatus = $"✘  Connection failed: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        config.Database.RunRcu         = RunRcu;
        config.Database.Host           = DbHost;
        config.Database.Port           = DbPort;
        config.Database.ServiceName    = ServiceName;
        config.Database.SysUsername    = SysUsername;
        config.Database.SysPassword    = SysPassword;   // encrypted by engine before persisting
        config.Database.SchemaPrefix   = SchemaPrefix;
        config.Database.SchemaPassword = SchemaPassword;
        config.Database.NlsCharset     = NlsCharset;
    }
}
