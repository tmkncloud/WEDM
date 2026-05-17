using WEDM.Domain.Models;

namespace WEDM.Domain.Interfaces;

/// <summary>Builds a compliance / security audit from configuration (no secret emission).</summary>
public interface ISecurityComplianceEvaluator
{
    ComplianceReport Evaluate(DeploymentConfiguration config);
}

/// <summary>
/// DPAPI-protected local secret vault.
///
/// Extended from the original two-method contract to support:
///   • Existence checks and key enumeration (for diagnostics)
///   • Vault-level metadata for DPAPI scope detection
///   • Entry deletion (cleanup after deployment completes)
/// </summary>
public interface ILocalSecretVault
{
    // ── Core read / write ─────────────────────────────────────────────────────

    /// <summary>Persists a named secret for the given deployment using DPAPI encryption.</summary>
    void Save(Guid deploymentId, string secretName, string plaintext);

    /// <summary>
    /// Retrieves and decrypts a named secret.
    /// Returns null when the entry does not exist or decryption fails.
    /// </summary>
    string? TryLoad(Guid deploymentId, string secretName);

    // ── Extended query API ────────────────────────────────────────────────────

    /// <summary>Returns true when a vault entry exists for the given name.</summary>
    bool Exists(Guid deploymentId, string secretName);

    /// <summary>
    /// Returns the list of secret names (aliases) stored for the given deployment.
    /// Returns an empty list when no vault file exists.
    /// </summary>
    IReadOnlyList<string> ListKeys(Guid deploymentId);

    /// <summary>
    /// Permanently removes a named secret from the vault.
    /// No-op when the entry does not exist.
    /// </summary>
    void Delete(Guid deploymentId, string secretName);

    /// <summary>
    /// Returns metadata-only diagnostics for the vault file of the given deployment.
    /// NEVER exposes plaintext values or encrypted blobs.
    /// </summary>
    VaultDiagnostics GetDiagnostics(Guid deploymentId);
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

/// <summary>
/// Governs the vault-backed secret lifecycle for recoverable deployment sessions.
///
/// Purpose: eliminate the R-03 bug where checkpoint persistence redacted
/// runtime secrets with "***REDACTED***", making resumed sessions unable to
/// authenticate against Oracle tools, WLST, RCU, or SSL endpoints.
///
/// Contract:
///   • PersistAndBind()  — called on every checkpoint save; vaults each secret
///     and replaces the config field with a vault-reference sentinel.
///   • Rehydrate()       — called on session resume; resolves every sentinel
///     back to its plaintext value from the DPAPI vault.
///   • ValidateForResume() — called before resume; returns a blocking result
///     if any required secret cannot be resolved.
///
/// Invariants:
///   • Plaintext values are NEVER written to checkpoint JSON.
///   • Legacy "***REDACTED***" values produce diagnostic errors, never silent failures.
///   • DPAPI scope mismatches (different user) produce actionable operator warnings.
///   • All diagnostics output aliases and field names only — no secret values.
/// </summary>
public interface ISecretRehydrationService
{
    // ── Checkpoint save ───────────────────────────────────────────────────────

    /// <summary>
    /// Persists each runtime secret from <paramref name="config"/> to the DPAPI vault
    /// under a deterministic alias, then replaces every secret field in <paramref name="config"/>
    /// with a vault-reference sentinel of the form "__WEDM_VAULT_REF:{alias}__".
    ///
    /// Call this on the cloned configuration just before writing the checkpoint to disk.
    /// The resulting config object is safe to serialize — it contains no plaintext secrets.
    ///
    /// Returns one binding per secret field processed.
    /// </summary>
    IReadOnlyList<SecretVaultBinding> PersistAndBind(DeploymentConfiguration config, Guid deploymentId);

    // ── Session resume ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves vault-reference sentinels in <paramref name="config"/> back to their
    /// plaintext values by decrypting the corresponding DPAPI vault entries.
    ///
    /// Mutates <paramref name="config"/> in-place: sentinel field values are replaced
    /// with the decrypted plaintext.
    ///
    /// Returns a result describing which secrets were resolved and which were not.
    /// Callers must check <see cref="SecretRehydrationResult.AllResolved"/> before proceeding.
    /// </summary>
    SecretRehydrationResult Rehydrate(DeploymentConfiguration config, Guid deploymentId);

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates that all required secrets in <paramref name="config"/> can be resolved
    /// from the DPAPI vault without actually writing any values into the config.
    ///
    /// Call before <see cref="Rehydrate"/> to produce operator-friendly diagnostics and
    /// block resume early when recovery is impossible.
    /// </summary>
    SecretRehydrationResult ValidateForResume(DeploymentConfiguration config, Guid deploymentId);

    // ── Inspection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when any secret field in <paramref name="config"/> contains a
    /// legacy "***REDACTED***" placeholder that cannot be recovered from the vault.
    /// </summary>
    bool HasLegacyPlaceholders(DeploymentConfiguration config);

    /// <summary>
    /// Returns true when any secret field in <paramref name="config"/> contains a
    /// vault-reference sentinel written by <see cref="PersistAndBind"/>.
    /// </summary>
    bool HasVaultReferences(DeploymentConfiguration config);

    /// <summary>
    /// Returns metadata-only diagnostics describing the current secret state of
    /// <paramref name="config"/> and the associated DPAPI vault.
    /// NEVER exposes plaintext values, encrypted blobs, or credential material.
    /// </summary>
    SecretRehydrationDiagnostics GetDiagnostics(DeploymentConfiguration config, Guid deploymentId);
}
