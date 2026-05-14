using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Security;

/// <summary>Heuristic security / compliance scoring (no secret values).</summary>
public sealed class SecurityComplianceEvaluator : ISecurityComplianceEvaluator
{
    private readonly ICertificateMaterialValidator _certs;

    public SecurityComplianceEvaluator(ICertificateMaterialValidator certs) => _certs = certs;

    public ComplianceReport Evaluate(DeploymentConfiguration config)
    {
        var report = new ComplianceReport
        {
            ConfigurationId = config.Id,
            Environment     = config.DeploymentEnvironment
        };

        var findings = new List<ComplianceFinding>();

        void Add(string id, string cat, string title, string detail, bool pass, int weight = 1)
            => findings.Add(new ComplianceFinding
            {
                Id       = id,
                Category = cat,
                Title    = title,
                Detail   = detail,
                Weight   = weight,
                Passed   = pass
            });

        Add("SEC-001", "Secrets", "Encrypted password mode",
            config.Security.UseEncryptedPasswords ? "UseEncryptedPasswords is enabled." : "Plaintext password handling is discouraged.",
            config.Security.UseEncryptedPasswords, 2);

        var vaultOk = config.DeploymentEnvironment != DeploymentEnvironmentKind.Prod
                      || config.Security.Secrets.PreferredStorage != SecretsStorageMode.ConfigOnlyRedactedLogging;
        Add("SEC-002", "Secrets", "Secrets storage preference",
            $"Storage mode: {config.Security.Secrets.PreferredStorage}",
            vaultOk, 1);

        Add("SEC-010", "Secrets", "Admin password length",
            "Administrative password should be at least 12 characters in non-DEV environments.",
            string.IsNullOrEmpty(config.Domain.AdminPassword) || config.Domain.AdminPassword.Length >= 12 || config.DeploymentEnvironment == DeploymentEnvironmentKind.Dev,
            2);

        var prodModeOk = config.DomainHardening.ProductionMode
                         || config.DeploymentEnvironment is DeploymentEnvironmentKind.Dev or DeploymentEnvironmentKind.Sit;
        Add("HRD-001", "Hardening", "Production domain mode",
            config.DomainHardening.ProductionMode ? "Production mode enabled for domain." : "Development-oriented domain mode.",
            prodModeOk, 2);

        Add("HRD-002", "Hardening", "Strict post-validation",
            config.DomainHardening.StrictPostValidation ? "Strict validation enabled." : "Non-strict validation.",
            config.DomainHardening.StrictPostValidation || config.DeploymentEnvironment != DeploymentEnvironmentKind.Prod,
            1);

        Add("NM-001", "NodeManager", "Node Manager transport",
            $"Node Manager type: {config.Domain.NodeManager.Type}",
            !string.Equals(config.Domain.NodeManager.Type, "Plain", StringComparison.OrdinalIgnoreCase) || config.DeploymentEnvironment != DeploymentEnvironmentKind.Prod,
            2);

        Add("WLST-001", "Automation", "Online WLST automation",
            config.DomainOnlineAutomation.Enabled ? "Online WLST automation enabled." : "Online automation disabled.",
            config.DomainOnlineAutomation.Enabled || config.DeploymentEnvironment == DeploymentEnvironmentKind.Dev,
            1);

        var ssl = config.Security.SslCertificates;
        if (!string.IsNullOrWhiteSpace(ssl.IdentityKeystorePath))
        {
            var vr = _certs.ValidateIdentityKeystore(ssl);
            Add("SSL-001", "SSL", "Identity PKCS#12", vr.Message, vr.Success, 3);
            if (vr is { Success: true, NotAfter: { } na })
            {
                var days = (na - DateTimeOffset.UtcNow).TotalDays;
                Add("SSL-002", "SSL", "Certificate validity window",
                    $"Identity certificate expires in {(int)days} days (minimum {ssl.MinimumCertificateValidityDays} required).",
                    days >= ssl.MinimumCertificateValidityDays, 2);
            }
        }
        else
        {
            Add("SSL-000", "SSL", "Custom identity keystore",
                "No PKCS#12 identity keystore configured — configure custom material for production.",
                config.DeploymentEnvironment != DeploymentEnvironmentKind.Prod, 1);
        }

        if (!string.IsNullOrWhiteSpace(ssl.TrustKeystorePath))
        {
            var exists = File.Exists(Path.GetFullPath(ssl.TrustKeystorePath));
            Add("SSL-010", "SSL", "Trust keystore present", exists ? "Trust keystore file found." : "Trust keystore path invalid.", exists, 1);
        }

        report.Findings = findings;

        static int CategoryScore(List<ComplianceFinding> list, string category)
        {
            var sub = list.Where(f => f.Category == category).ToList();
            if (sub.Count == 0) return 100;
            var passedWeight = sub.Where(f => f.Passed).Sum(f => f.Weight);
            var totalWeight  = sub.Sum(f => f.Weight);
            return totalWeight == 0 ? 100 : (int)Math.Round(100.0 * passedWeight / totalWeight);
        }

        report.HardeningScore           = CategoryScore(findings, "Hardening");
        report.SecretsManagementScore   = CategoryScore(findings, "Secrets");
        report.SslReadinessScore        = CategoryScore(findings, "SSL");
        report.ProductionReadinessScore = Math.Min(report.HardeningScore, Math.Min(report.SecretsManagementScore, report.SslReadinessScore));
        var passW = findings.Where(f => f.Passed).Sum(f => f.Weight);
        var totW  = findings.Sum(f => f.Weight);
        report.OverallScore = totW == 0 ? 100 : (int)Math.Round(100.0 * passW / totW);
        return report;
    }
}
