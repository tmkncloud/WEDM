using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>Validates PKCS#12 material and optionally persists secrets to the DPAPI vault (never logs secret values).</summary>
public sealed class ValidateSecuritySecretsAndSslStep : IStepExecutor
{
    private readonly ICertificateMaterialValidator _certs;
    private readonly ILocalSecretVault             _vault;
    private readonly ILoggingService               _log;

    public ValidateSecuritySecretsAndSslStep(
        ICertificateMaterialValidator certs,
        ILocalSecretVault vault,
        ILoggingService log)
    {
        _certs = certs;
        _vault = vault;
        _log   = log;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var ssl = config.Security.SslCertificates;
        if (!string.IsNullOrWhiteSpace(ssl.IdentityKeystorePath))
        {
            var vr = _certs.ValidateIdentityKeystore(ssl);
            if (!vr.Success)
                return Task.FromResult(StepExecutionResult.Fail($"SSL identity validation failed: {vr.Message}", 60));
            _log.Info("SSL identity keystore validation succeeded.", "Security.SSL");
        }

        if (config.Security.Secrets.PersistToDpapiVaultAfterValidation)
        {
            if (!string.IsNullOrEmpty(config.Domain.AdminPassword))
                _vault.Save(config.Id, "domain.admin", config.Domain.AdminPassword);
            if (!string.IsNullOrEmpty(config.Database.SysPassword))
                _vault.Save(config.Id, "database.sys", config.Database.SysPassword);
            if (!string.IsNullOrEmpty(config.Database.SchemaPassword))
                _vault.Save(config.Id, "database.schema", config.Database.SchemaPassword);
            _log.Info("Operational secrets mirrored to DPAPI vault (session configuration unchanged).", "Security.Secrets");
        }

        return Task.FromResult(StepExecutionResult.Ok("Security secrets / SSL validation completed."));
    }
}

/// <summary>Evaluates compliance and writes HTML + JSON security reports (no credentials).</summary>
public sealed class GenerateSecurityComplianceAuditStep : IStepExecutor
{
    private readonly ISecurityComplianceEvaluator _eval;
    private readonly ISecurityReportWriter         _writer;
    private readonly ILoggingService               _log;

    public GenerateSecurityComplianceAuditStep(
        ISecurityComplianceEvaluator eval,
        ISecurityReportWriter writer,
        ILoggingService log)
    {
        _eval   = eval;
        _writer = writer;
        _log    = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(config.Paths.ReportsDirectory);
        var report = _eval.Evaluate(config);
        var stamp  = $"{config.Id:N}";
        var html   = Path.Combine(config.Paths.ReportsDirectory, $"wedm-security-{stamp}.html");
        var json   = Path.Combine(config.Paths.ReportsDirectory, $"wedm-security-{stamp}.json");
        await _writer.WriteHtmlAsync(report, html, cancellationToken).ConfigureAwait(false);
        await _writer.WriteJsonAsync(report, json, cancellationToken).ConfigureAwait(false);
        _log.Info($"Security compliance reports written: {html}", "Security.Reporting");
        return StepExecutionResult.Ok($"{html};{json}");
    }
}
