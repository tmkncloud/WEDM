using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Base;

namespace WEDM.UI.ViewModels.Wizard;

/// <summary>
/// Security, secrets storage, SSL material, and live compliance preview.
/// </summary>
public sealed partial class SecurityComplianceViewModel : WizardStepViewModel
{
    private readonly ISecurityComplianceEvaluator _eval;
    private readonly ICertificateMaterialValidator _certs;
    private readonly IPowerShellExecutor          _ps;
    private readonly ILoggingService              _log;

    private DeploymentConfiguration? _session;

    [ObservableProperty] private SecretsStorageMode _secretsStorageMode = SecretsStorageMode.ConfigOnlyRedactedLogging;

    [ObservableProperty] private bool _persistSecretsToDpapiVault;

    [ObservableProperty] private string _identityKeystorePath = string.Empty;

    [ObservableProperty] private string _identityKeystorePassword = string.Empty;

    [ObservableProperty] private string _trustKeystorePath = string.Empty;

    [ObservableProperty] private string _trustKeystorePassword = string.Empty;

    [ObservableProperty] private string _identityKeyAlias = "server";

    [ObservableProperty] private int _minimumCertValidityDays = 30;

    [ObservableProperty] private bool _generateDevSelfSignedIfMissing;

    [ObservableProperty] private int _overallScore;

    [ObservableProperty] private int _secretsScore;

    [ObservableProperty] private int _sslScore;

    [ObservableProperty] private int _hardeningScore;

    [ObservableProperty] private string _previewStatus = "Click 'Refresh compliance preview' to score the current plan.";

    public IReadOnlyList<SecretsStorageMode> SecretsStorageModes { get; } = Enum.GetValues<SecretsStorageMode>().ToArray();

    public override bool CanProceed => true;

    public SecurityComplianceViewModel(
        ISecurityComplianceEvaluator eval,
        ICertificateMaterialValidator certs,
        IPowerShellExecutor ps,
        ILoggingService log)
    {
        _eval  = eval;
        _certs = certs;
        _ps    = ps;
        _log   = log;
        StepTitle       = "Security & Compliance";
        StepDescription = "Secrets handling, SSL keystores, and compliance scoring.";
        StepIcon        = "🛡";
    }

    public override Task OnNavigatingToAsync(DeploymentConfiguration config)
    {
        _session = config;
        SecretsStorageMode           = config.Security.Secrets.PreferredStorage;
        PersistSecretsToDpapiVault   = config.Security.Secrets.PersistToDpapiVaultAfterValidation;
        IdentityKeystorePath         = config.Security.SslCertificates.IdentityKeystorePath;
        IdentityKeystorePassword     = config.Security.SslCertificates.IdentityKeystorePassword;
        TrustKeystorePath            = config.Security.SslCertificates.TrustKeystorePath;
        TrustKeystorePassword        = config.Security.SslCertificates.TrustKeystorePassword;
        IdentityKeyAlias             = config.Security.SslCertificates.IdentityPrivateKeyAlias;
        MinimumCertValidityDays       = config.Security.SslCertificates.MinimumCertificateValidityDays;
        GenerateDevSelfSignedIfMissing = config.Security.SslCertificates.GenerateDevSelfSignedIfMissing;
        PreviewStatus = "Click 'Refresh compliance preview' to score the current plan.";
        return Task.CompletedTask;
    }

    public override void ApplyToConfiguration(DeploymentConfiguration config)
    {
        config.Security.Secrets.PreferredStorage                    = SecretsStorageMode;
        config.Security.Secrets.PersistToDpapiVaultAfterValidation = PersistSecretsToDpapiVault;
        config.Security.SslCertificates.IdentityKeystorePath       = IdentityKeystorePath;
        config.Security.SslCertificates.IdentityKeystorePassword   = IdentityKeystorePassword;
        config.Security.SslCertificates.TrustKeystorePath          = TrustKeystorePath;
        config.Security.SslCertificates.TrustKeystorePassword        = TrustKeystorePassword;
        config.Security.SslCertificates.IdentityPrivateKeyAlias     = IdentityKeyAlias;
        config.Security.SslCertificates.MinimumCertificateValidityDays = MinimumCertValidityDays;
        config.Security.SslCertificates.GenerateDevSelfSignedIfMissing = GenerateDevSelfSignedIfMissing;
    }

    [RelayCommand]
    private void RefreshCompliancePreview()
    {
        if (_session is null) return;
        ApplyToConfiguration(_session);
        var r = _eval.Evaluate(_session);
        OverallScore   = r.OverallScore;
        SecretsScore   = r.SecretsManagementScore;
        SslScore       = r.SslReadinessScore;
        HardeningScore = r.HardeningScore;
        PreviewStatus  = $"Overall {r.OverallScore}% — {r.Findings.Count(f => !f.Passed)} open finding(s). Reports are written at deployment end.";
    }

    [RelayCommand]
    private async Task ValidateIdentityKeystoreAsync()
    {
        if (_session is null) return;
        ApplyToConfiguration(_session);
        var ssl = _session.Security.SslCertificates;
        if (string.IsNullOrWhiteSpace(ssl.IdentityKeystorePath))
        {
            PreviewStatus = "Set an identity keystore path first.";
            return;
        }

        SetBusy(true, "Validating PKCS#12…");
        try
        {
            await Task.Yield();
            var vr = _certs.ValidateIdentityKeystore(ssl);
            PreviewStatus = vr.Success
                ? $"Keystore OK — {vr.Subject}"
                : vr.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    [RelayCommand]
    private async Task GenerateDevSelfSignedPfxAsync()
    {
        if (_session is null) return;
        if (_session.DeploymentEnvironment == DeploymentEnvironmentKind.Prod)
        {
            PreviewStatus = "Self-signed generation is disabled for PROD profile.";
            return;
        }

        Directory.CreateDirectory(_session.Paths.ReportsDirectory);
        var pfx = Path.Combine(_session.Paths.ReportsDirectory, $"wedm-dev-identity-{_session.Id:N}.pfx");
        var dns = _session.Network.Hostname.Replace("'", "''");
        var pfxEsc = pfx.Replace("'", "''");
        var body = $@"
$pwd = ConvertTo-SecureString -String 'changeit' -AsPlainText -Force
$cert = New-SelfSignedCertificate -DnsName '{dns}' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyExportPolicy Exportable -KeySpec KeyExchange
Export-PfxCertificate -Cert $cert -FilePath '{pfxEsc}' -Password $pwd | Out-Null
Remove-Item -Path $cert.PSPath -Force -ErrorAction SilentlyContinue
Write-Output 'OK'
";
        SetBusy(true, "Generating DEV self-signed PFX…");
        try
        {
            var r = await _ps.ExecuteCommandAsync(body.Trim(), cancellationToken: default,
                operationTimeout: TimeSpan.FromMinutes(2));
            if (!r.Success || !File.Exists(pfx))
            {
                PreviewStatus = $"PFX generation failed: {r.Errors}";
                return;
            }

            IdentityKeystorePath     = pfx;
            IdentityKeystorePassword = "changeit";
            ApplyToConfiguration(_session);
            PreviewStatus = $"DEV PFX created at {pfx} (password: changeit — rotate before any non-DEV use).";
            _log.Info("DEV self-signed PFX written under the configured reports directory.", "Security.SSL");
        }
        finally
        {
            SetBusy(false);
        }
    }
}
