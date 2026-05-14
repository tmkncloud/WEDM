using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Builds a compliance / security audit from configuration (no secret emission).</summary>
public interface ISecurityComplianceEvaluator
{
    ComplianceReport Evaluate(DeploymentConfiguration config);
}

/// <summary>Persists named secrets using DPAPI (machine-local).</summary>
public interface ILocalSecretVault
{
    void Save(Guid deploymentId, string secretName, string plaintext);

    string? TryLoad(Guid deploymentId, string secretName);
}

/// <summary>Validates PKCS#12 / PFX identity material (password never logged).</summary>
public interface ICertificateMaterialValidator
{
    CertificateValidationResult ValidateIdentityKeystore(SslCertificateConfiguration ssl);
}

/// <summary>HTML + JSON security / compliance artefacts.</summary>
public interface ISecurityReportWriter
{
    Task WriteHtmlAsync(ComplianceReport report, string outputPath, CancellationToken cancellationToken = default);

    Task WriteJsonAsync(ComplianceReport report, string outputPath, CancellationToken cancellationToken = default);
}
