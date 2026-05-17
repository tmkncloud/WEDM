using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Security;

/// <summary>
/// Implements vault-backed secret lifecycle for recoverable deployment sessions.
///
/// This service eliminates the R-03 bug: prior to this implementation, every
/// deployment checkpoint persisted "***REDACTED***" in all credential fields.
/// On resume, WLST, RCU, SSL, and database steps would immediately fail with
/// authentication errors because no real credentials were available.
///
/// The new lifecycle:
///   Save path  → <see cref="PersistAndBind"/> encrypts each secret into the DPAPI
///                vault and replaces the config field with a sentinel marker.
///   Load path  → <see cref="Rehydrate"/> detects sentinels, decrypts from vault,
///                and restores plaintext into the live config before execution begins.
///   Validation → <see cref="ValidateForResume"/> dry-runs the load path and returns
///                a structured error if any required secret cannot be resolved, so
///                the resume is blocked before any Oracle step can fail silently.
/// </summary>
public sealed class SecretRehydrationService : ISecretRehydrationService
{
    private readonly ILocalSecretVault _vault;

    public SecretRehydrationService(ILocalSecretVault vault)
    {
        _vault = vault;
    }

    // ── Secret field descriptor table ─────────────────────────────────────────
    //
    // Every credential-holding field in DeploymentConfiguration is registered here.
    // New secret fields must be added to this list — the service will automatically
    // handle vaulting and rehydration for them.
    //
    // Fields marked isRequired=true will cause resume validation to fail if they
    // cannot be resolved and are non-empty in the checkpoint (i.e. were set before crash).

    private sealed record SecretFieldDescriptor(
        string         Alias,
        SecretKind     Kind,
        string         Description,
        bool           IsRequired,
        Func<DeploymentConfiguration, string?> Get,
        Action<DeploymentConfiguration, string> Set);

    private static readonly IReadOnlyList<SecretFieldDescriptor> SecretFields =
    [
        new(
            Alias:       "domain.admin.password",
            Kind:        SecretKind.AdminPassword,
            Description: "WebLogic Administration Server password",
            IsRequired:  true,
            Get:         c => c.Domain.AdminPassword,
            Set:         (c, v) => c.Domain.AdminPassword = v),

        new(
            Alias:       "database.sys.password",
            Kind:        SecretKind.SysPassword,
            Description: "Oracle SYS account password (RCU prerequisite)",
            IsRequired:  false, // optional — only required when database is configured
            Get:         c => c.Database.SysPassword,
            Set:         (c, v) => c.Database.SysPassword = v),

        new(
            Alias:       "database.schema.password",
            Kind:        SecretKind.SchemaPassword,
            Description: "RCU schema owner password",
            IsRequired:  false,
            Get:         c => c.Database.SchemaPassword,
            Set:         (c, v) => c.Database.SchemaPassword = v),

        new(
            Alias:       "security.ssl.identity.keystorepassword",
            Kind:        SecretKind.IdentityKeystorePassword,
            Description: "PKCS#12 identity keystore password",
            IsRequired:  false,
            Get:         c => c.Security.SslCertificates.IdentityKeystorePassword,
            Set:         (c, v) => c.Security.SslCertificates.IdentityKeystorePassword = v),

        new(
            Alias:       "security.ssl.trust.keystorepassword",
            Kind:        SecretKind.TrustKeystorePassword,
            Description: "Trust keystore password",
            IsRequired:  false,
            Get:         c => c.Security.SslCertificates.TrustKeystorePassword,
            Set:         (c, v) => c.Security.SslCertificates.TrustKeystorePassword = v),
    ];

    // ── PersistAndBind ────────────────────────────────────────────────────────

    public IReadOnlyList<SecretVaultBinding> PersistAndBind(
        DeploymentConfiguration config,
        Guid deploymentId)
    {
        var bindings = new List<SecretVaultBinding>();

        foreach (var field in SecretFields)
        {
            var currentValue = field.Get(config);

            // Skip empty fields and existing sentinels (already bound on a prior checkpoint)
            if (string.IsNullOrEmpty(currentValue))
                continue;

            if (SecretReference.IsSentinel(currentValue))
            {
                // Already a sentinel — vault entry should already exist; report as resolved
                bindings.Add(new SecretVaultBinding
                {
                    Reference = BuildReference(field, deploymentId),
                    Source    = SecretResolutionSource.DpapiVault,
                    IsResolved = _vault.Exists(deploymentId, field.Alias),
                    ValidationError = _vault.Exists(deploymentId, field.Alias) ? null
                        : $"Sentinel present in config but vault entry missing for '{field.Alias}'."
                });
                continue;
            }

            if (SecretReference.IsLegacyPlaceholder(currentValue))
            {
                // Cannot vault a placeholder — this is already lost data
                bindings.Add(new SecretVaultBinding
                {
                    Reference = BuildReference(field, deploymentId),
                    Source    = SecretResolutionSource.LegacyPlaceholder,
                    IsResolved = false,
                    ValidationError = $"Field '{field.Alias}' contains a legacy redacted placeholder. " +
                                      "The original secret value is not recoverable."
                });
                continue;
            }

            // Plaintext value — persist to vault and replace with sentinel
            try
            {
                _vault.Save(deploymentId, field.Alias, currentValue);
                field.Set(config, SecretReference.ToSentinel(field.Alias));

                bindings.Add(new SecretVaultBinding
                {
                    Reference  = BuildReference(field, deploymentId),
                    Source     = SecretResolutionSource.DpapiVault,
                    IsResolved = true,
                });
            }
            catch (Exception ex)
            {
                // Vault write failed — fall back to NOT replacing the value
                // so the config is still usable for this session.  Log the issue
                // without emitting the secret value.
                bindings.Add(new SecretVaultBinding
                {
                    Reference = BuildReference(field, deploymentId),
                    Source    = SecretResolutionSource.ConfigPlaintext,
                    IsResolved = false,
                    ValidationError = $"Failed to persist '{field.Alias}' to vault: {ex.GetType().Name} — {ex.Message}"
                });
            }
        }

        return bindings.AsReadOnly();
    }

    // ── Rehydrate ─────────────────────────────────────────────────────────────

    public SecretRehydrationResult Rehydrate(DeploymentConfiguration config, Guid deploymentId)
        => RehydrateCore(config, deploymentId, mutate: true);

    // ── ValidateForResume ─────────────────────────────────────────────────────

    public SecretRehydrationResult ValidateForResume(DeploymentConfiguration config, Guid deploymentId)
        => RehydrateCore(config, deploymentId, mutate: false);

    // ── Inspection ────────────────────────────────────────────────────────────

    public bool HasLegacyPlaceholders(DeploymentConfiguration config)
        => SecretFields.Any(f => SecretReference.IsLegacyPlaceholder(f.Get(config)));

    public bool HasVaultReferences(DeploymentConfiguration config)
        => SecretFields.Any(f => SecretReference.IsSentinel(f.Get(config)));

    public SecretRehydrationDiagnostics GetDiagnostics(
        DeploymentConfiguration config,
        Guid deploymentId)
    {
        var issues           = new List<string>();
        var remediationSteps = new List<string>();
        int vaultBound       = 0;
        int plainText        = 0;
        int placeholder      = 0;
        int empty            = 0;

        foreach (var field in SecretFields)
        {
            var value = field.Get(config);
            if (string.IsNullOrEmpty(value))
            {
                empty++;
            }
            else if (SecretReference.IsSentinel(value))
            {
                vaultBound++;
                if (!_vault.Exists(deploymentId, field.Alias))
                {
                    issues.Add($"Vault entry missing for '{field.Alias}' (field has sentinel but no vault entry).");
                    remediationSteps.Add($"Re-enter the '{field.Description}' before resuming, or restore the vault file.");
                }
            }
            else if (SecretReference.IsLegacyPlaceholder(value))
            {
                placeholder++;
                issues.Add($"Field '{field.Alias}' ({field.Description}) contains a legacy placeholder — unrecoverable.");
                remediationSteps.Add($"Re-enter the '{field.Description}' — the original value was overwritten by a redaction pass.");
            }
            else
            {
                // Runtime plaintext — not yet vaulted
                plainText++;
            }
        }

        var vaultDiag = _vault.GetDiagnostics(deploymentId);

        bool resumeReady = issues.Count == 0
            && placeholder == 0
            && (vaultBound > 0 || plainText > 0); // at least some secrets are accessible

        if (!vaultDiag.ScopeCompatible && vaultDiag.VaultFileExists)
        {
            issues.Add(vaultDiag.ScopeWarning ?? "DPAPI scope incompatibility detected.");
            remediationSteps.Add("Run WEDM as the original user account, or re-enter all credentials manually.");
            resumeReady = false;
        }

        return new SecretRehydrationDiagnostics
        {
            DeploymentId      = deploymentId,
            TotalSecretFields = SecretFields.Count,
            VaultBoundFields  = vaultBound,
            PlainTextFields   = plainText,
            PlaceholderFields = placeholder,
            EmptyFields       = empty,
            ResumeReady       = resumeReady,
            Vault             = vaultDiag,
            Issues            = issues.AsReadOnly(),
            RemediationSteps  = remediationSteps.AsReadOnly(),
        };
    }

    // ── Core rehydration logic ────────────────────────────────────────────────

    private SecretRehydrationResult RehydrateCore(
        DeploymentConfiguration config,
        Guid deploymentId,
        bool mutate)
    {
        var bindings         = new List<SecretVaultBinding>();
        var missing          = new List<string>();
        var placeholder      = new List<string>();
        var remediationSteps = new List<string>();

        var vaultDiag = _vault.GetDiagnostics(deploymentId);
        string? scopeWarning = vaultDiag.ScopeCompatible ? null : vaultDiag.ScopeWarning;

        foreach (var field in SecretFields)
        {
            var currentValue = field.Get(config);

            // ── Case 1: Vault reference sentinel ──────────────────────────────
            if (SecretReference.IsSentinel(currentValue))
            {
                var alias = SecretReference.ExtractAlias(currentValue);
                if (alias is null)
                {
                    bindings.Add(Binding(field, deploymentId, SecretResolutionSource.NotResolved, false,
                        $"Malformed vault sentinel in field '{field.Alias}'."));
                    missing.Add(field.Alias);
                    remediationSteps.Add($"Re-enter the '{field.Description}'.");
                    continue;
                }

                var plaintext = _vault.TryLoad(deploymentId, alias);
                if (plaintext is null)
                {
                    bindings.Add(Binding(field, deploymentId, SecretResolutionSource.NotResolved, false,
                        $"Vault entry '{alias}' not found or could not be decrypted " +
                        (scopeWarning is not null ? "(DPAPI scope mismatch likely)" : "(vault entry missing)")));
                    missing.Add(field.Alias);
                    remediationSteps.Add(scopeWarning is not null
                        ? $"Re-run WEDM under the original user account to access the DPAPI vault for '{field.Description}'."
                        : $"Vault entry for '{field.Description}' is missing — re-enter the value before resuming.");
                    continue;
                }

                if (mutate)
                    field.Set(config, plaintext);

                bindings.Add(Binding(field, deploymentId, SecretResolutionSource.DpapiVault, true, null));
                continue;
            }

            // ── Case 2: Legacy placeholder ────────────────────────────────────
            if (SecretReference.IsLegacyPlaceholder(currentValue))
            {
                placeholder.Add(field.Alias);
                bindings.Add(Binding(field, deploymentId, SecretResolutionSource.LegacyPlaceholder, false,
                    $"Field '{field.Alias}' ({field.Description}) contains a legacy redacted placeholder " +
                    "written by an older WEDM version. The original value cannot be recovered."));
                remediationSteps.Add(
                    $"Re-enter the '{field.Description}'. The credential was permanently overwritten " +
                    "during a prior checkpoint save by the old redaction system.");
                continue;
            }

            // ── Case 3: Empty / null ──────────────────────────────────────────
            if (string.IsNullOrEmpty(currentValue))
            {
                // Optional fields being empty is acceptable — not a resume blocker
                bindings.Add(Binding(field, deploymentId, SecretResolutionSource.ConfigPlaintext, true,
                    null)); // "resolved" in the sense that it's intentionally absent
                continue;
            }

            // ── Case 4: Runtime plaintext (not yet vaulted) ───────────────────
            // This happens when a fresh deployment is resumed from a checkpoint that
            // was saved BEFORE PersistAndBind was wired in (upgrade scenario).
            // The plaintext is already present — no action needed.
            bindings.Add(Binding(field, deploymentId, SecretResolutionSource.ConfigPlaintext, true, null));
        }

        bool allResolved = missing.Count == 0
                        && placeholder.Count == 0
                        && scopeWarning is null;

        if (!allResolved)
        {
            if (missing.Count > 0)
                remediationSteps.Insert(0,
                    $"{missing.Count} secret(s) could not be resolved from the vault. " +
                    "These must be re-entered before the deployment can resume.");

            if (placeholder.Count > 0)
                remediationSteps.Insert(0,
                    $"{placeholder.Count} field(s) contain legacy '***REDACTED***' placeholders. " +
                    "These were written by an older WEDM version and cannot be recovered. " +
                    "A new deployment must be started instead of resuming this session.");
        }

        return new SecretRehydrationResult
        {
            AllResolved      = allResolved,
            Bindings         = bindings.AsReadOnly(),
            MissingSecrets   = missing.AsReadOnly(),
            PlaceholderSecrets = placeholder.AsReadOnly(),
            DpapiScopeWarning = scopeWarning,
            RemediationSteps = remediationSteps.Distinct().ToList().AsReadOnly(),
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SecretReference BuildReference(SecretFieldDescriptor field, Guid deploymentId)
        => new()
        {
            Alias        = field.Alias,
            DeploymentId = deploymentId,
            Kind         = field.Kind,
            Description  = field.Description,
        };

    private static SecretVaultBinding Binding(
        SecretFieldDescriptor  field,
        Guid                   deploymentId,
        SecretResolutionSource source,
        bool                   resolved,
        string?                error)
        => new()
        {
            Reference       = BuildReference(field, deploymentId),
            Source          = source,
            IsResolved      = resolved,
            ValidationError = error,
        };
}
