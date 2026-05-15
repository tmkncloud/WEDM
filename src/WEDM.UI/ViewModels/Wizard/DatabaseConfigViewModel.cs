using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Net.Sockets;
using WEDM.Domain.Models;
using WEDM.UI.Services;
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

    [ObservableProperty] private string _dbHostError = string.Empty;
    [ObservableProperty] private string _dbPortError = string.Empty;
    [ObservableProperty] private string _serviceNameError = string.Empty;
    [ObservableProperty] private string _sysUsernameError = string.Empty;
    [ObservableProperty] private string _sysPasswordError = string.Empty;
    [ObservableProperty] private string _schemaPrefixError = string.Empty;
    [ObservableProperty] private string _schemaPasswordError = string.Empty;

    public IReadOnlyList<string> NlsCharsets { get; } =
    [
        "AL32UTF8",
        "UTF8",
        "WE8MSWIN1252",
        "AR8MSWIN1256",
        "AL16UTF16",
        "EE8ISO8859P2",
        "EE8MSWIN1250",
        "JA16SJIS",
        "ZHS16GBK",
        "KO16MSWIN949",
        "TH8TISASCII",
        "CL8ISO8859P1",
    ];

    public override bool CanProceed =>
        !RunRcu ||
        (string.IsNullOrEmpty(DbHostError) &&
         string.IsNullOrEmpty(DbPortError) &&
         string.IsNullOrEmpty(ServiceNameError) &&
         string.IsNullOrEmpty(SysUsernameError) &&
         string.IsNullOrEmpty(SysPasswordError) &&
         string.IsNullOrEmpty(SchemaPrefixError) &&
         string.IsNullOrEmpty(SchemaPasswordError) &&
         !IsBusy);

    partial void OnRunRcuChanged(bool value) => ValidateFields();
    partial void OnDbHostChanged(string value) => ValidateFields();
    partial void OnDbPortChanged(int value) => ValidateFields();
    partial void OnServiceNameChanged(string value) => ValidateFields();
    partial void OnSysUsernameChanged(string value) => ValidateFields();
    partial void OnSysPasswordChanged(string value) => ValidateFields();
    partial void OnSchemaPrefixChanged(string value) => ValidateFields();
    partial void OnSchemaPasswordChanged(string value) => ValidateFields();

    protected override void RunStepValidation() => ValidateFields();

    private void ValidateFields()
    {
        if (!RunRcu)
        {
            DbHostError = DbPortError = ServiceNameError = SysUsernameError =
                SysPasswordError = SchemaPrefixError = SchemaPasswordError = string.Empty;
            IsValid = true;
            OnPropertyChanged(nameof(CanProceed));
            return;
        }

        DbHostError = WizardValidationHelper.IsRequiredText(DbHost, "Database host", out var h) ? string.Empty : h;
        DbPortError = WizardValidationHelper.IsValidPort(DbPort, out var p) ? string.Empty : p;
        ServiceNameError = WizardValidationHelper.IsRequiredText(ServiceName, "Service name", out var s) ? string.Empty : s;
        SysUsernameError = WizardValidationHelper.IsRequiredText(SysUsername, "DBA user", out var u) ? string.Empty : u;
        SysPasswordError = WizardValidationHelper.IsRequiredText(SysPassword, "DBA password", out var pw) ? string.Empty : pw;
        SchemaPrefixError = WizardValidationHelper.IsRequiredText(SchemaPrefix, "Schema prefix", out var sp) ? string.Empty : sp;
        SchemaPasswordError = WizardValidationHelper.IsRequiredText(SchemaPassword, "Schema password", out var sch) ? string.Empty : sch;
        IsValid = CanProceed;
        OnPropertyChanged(nameof(CanProceed));
        OnPropertyChanged(nameof(HasStepValidationError));
    }

    public DatabaseConfigViewModel()
    {
        StepTitle       = "Database";
        StepDescription = "Configure Oracle Database for RCU schema creation.";
        StepIcon        = "🗄️";
        ValidateFields();
    }

    public override Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        ValidateFields();
        return Task.CompletedTask;
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
