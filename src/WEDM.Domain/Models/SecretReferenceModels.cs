using System.Text.Json.Serialization;

namespace WEDM.Domain.Models;

// ═══════════════════════════════════════════════════════════════════════════════
// Secret Reference Models
// ═══════════════════════════════════════════════════════════════════════════════
//
// These models replace redacted placeholders in deployment checkpoints.
//
// Checkpoint lifecycle:
//   1. Deployment starts — config holds runtime plaintext in memory.
//   2. Checkpoint is written — ISecretRehydrationService.PersistAndBind() is called:
//        a. Each secret field is saved to the DPAPI vault under a deterministic alias.
//        b. The config field is replaced with a vault reference sentinel:
//             domain.AdminPassword → "__WEDM_VAULT_REF:domain.admin.password__"
//   3. Crash / restart — checkpoint is loaded from disk.
//   4. Resume — ISecretRehydrationService.Rehydrate() is called:
//        a. Each sentinel is detected and looked up in the DPAPI vault.
//        b. The decrypted value is written back into the live config.
//   5. Resume validation — if any required secret cannot be resolved, resume
//      is blocked with a structured error and operator remediation guidance.
//
// Invariants:
//   • Plaintext is NEVER written to the checkpoint JSON file.
//   • Legacy "***REDACTED***" placeholders are detected and produce blocking errors.
//   • DPAPI scope mismatches (different user/machine) are detected and warned.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Discriminates the category of secret stored in a vault binding.
/// Used for diagnostics and structured auditing — never contains secret values.
/// </summary>
public enum SecretKind
{
    /// <summary>WebLogic Administration Server password.</summary>
    AdminPassword,

    /// <summary>Oracle database SYS account password (RCU prerequisite).</summary>
    SysPassword,

    /// <summary>RCU schema owner password.</summary>
    SchemaPassword,

    /// <summary>PKCS#12 identity keystore password.</summary>
    IdentityKeystorePassword,

    /// <summary>Trust keystore password.</summary>
    TrustKeystorePassword,

    /// <summary>NodeManager password.</summary>
    NodeManagerPassword,

    /// <summary>JDBC datasource password.</summary>
    JdbcPassword,

    /// <summary>Any other credential not covered by a specific kind.</summary>
    Generic
}

/// <summary>
/// Indicates where a secret value came from during rehydration.
/// </summary>
public enum SecretResolutionSource
{
    /// <summary>Secret was decrypted from the DPAPI vault on this machine/user.</summary>
    DpapiVault,

    /// <summary>
    /// Secret value is available as runtime plaintext in configuration
    /// (no vault reference present — not yet checkpointed).
    /// </summary>
    ConfigPlaintext,

    /// <summary>Vault entry was not found — secret cannot be resolved.</summary>
    NotResolved,

    /// <summary>
    /// Checkpoint contains a legacy "***REDACTED***" placeholder written by
    /// older WEDM versions. Recovery is not possible — operator must re-enter.
    /// </summary>
    LegacyPlaceholder,
}

/// <summary>
/// Lightweight reference to a named secret stored in the DPAPI vault.
/// This is the ONLY credential representation persisted in deployment checkpoints.
///
/// NEVER contains:
///   • Plaintext passwords
///   • Encrypted blobs
///   • Encoded credentials
///   • Certificate material
/// </summary>
public sealed class SecretReference
{
    /// <summary>
    /// Stable vault alias for deterministic recovery.
    /// Format: "{category}.{name}" — e.g. "domain.admin.password".
    /// </summary>
    [JsonPropertyName("alias")]
    public string Alias { get; init; } = string.Empty;

    /// <summary>The deployment configuration ID this secret is bound to.</summary>
    [JsonPropertyName("deploymentId")]
    public Guid DeploymentId { get; init; }

    /// <summary>Semantic kind of the credential.</summary>
    [JsonPropertyName("kind")]
    public SecretKind Kind { get; init; }

    /// <summary>Human-readable description. NEVER contains the secret value.</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>UTC time this reference was created (when the secret was first vaulted).</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // ── Sentinel helpers ──────────────────────────────────────────────────────

    private const string SentinelPrefix = "__WEDM_VAULT_REF:";
    private const string SentinelSuffix = "__";

    /// <summary>
    /// Returns the sentinel string that replaces the plaintext value in a checkpoint.
    /// Format: "__WEDM_VAULT_REF:{alias}__"
    /// </summary>
    public static string ToSentinel(string alias)
        => $"{SentinelPrefix}{alias}{SentinelSuffix}";

    /// <summary>Returns true when the value is a vault reference sentinel.</summary>
    public static bool IsSentinel(string? value)
        => value is not null
           && value.StartsWith(SentinelPrefix, StringComparison.Ordinal)
           && value.EndsWith(SentinelSuffix, StringComparison.Ordinal)
           && value.Length > SentinelPrefix.Length + SentinelSuffix.Length;

    /// <summary>
    /// Extracts the alias from a sentinel string.
    /// Returns null if <paramref name="sentinel"/> is not a valid sentinel.
    /// </summary>
    public static string? ExtractAlias(string? sentinel)
    {
        if (!IsSentinel(sentinel)) return null;
        return sentinel![SentinelPrefix.Length..^SentinelSuffix.Length];
    }

    /// <summary>
    /// Returns true when the value is a legacy redacted placeholder
    /// (written by older WEDM that did not support vault references).
    /// Such values cannot be recovered from the vault.
    /// </summary>
    public static bool IsLegacyPlaceholder(string? value)
        => value is "***REDACTED***" or "***" or "REDACTED" or "***PASSWORD***";

    /// <summary>
    /// Returns true when the field contains a value that requires rehydration
    /// (sentinel) or that indicates recovery may be impossible (legacy placeholder).
    /// </summary>
    public static bool NeedsResolution(string? value)
        => !string.IsNullOrEmpty(value) && (IsSentinel(value) || IsLegacyPlaceholder(value));
}

/// <summary>
/// Binding between a <see cref="SecretReference"/> and its runtime resolution state.
/// Produced by <see cref="WEDM.Domain.Interfaces.ISecretRehydrationService.Rehydrate"/>.
/// </summary>
public sealed class SecretVaultBinding
{
    public SecretReference Reference { get; init; } = new();

    /// <summary>Where the secret value was sourced during rehydration.</summary>
    public SecretResolutionSource Source { get; init; }

    /// <summary>True when the secret was successfully resolved and injected into config.</summary>
    public bool IsResolved { get; init; }

    /// <summary>
    /// Validation error message when <see cref="IsResolved"/> is false.
    /// NEVER contains the secret value, encrypted blob, or any credential material.
    /// </summary>
    public string? ValidationError { get; init; }
}

/// <summary>
/// Result of a secret rehydration pass over a loaded <see cref="DeploymentConfiguration"/>.
///
/// If <see cref="AllResolved"/> is false, resume must be blocked.
/// The operator should be shown <see cref="RemediationSteps"/> to understand
/// what manual action is required.
/// </summary>
public sealed class SecretRehydrationResult
{
    /// <summary>
    /// True only when every required secret field was resolved successfully.
    /// A false value means resume MUST be blocked.
    /// </summary>
    public bool AllResolved { get; init; }

    /// <summary>All secret bindings examined during this rehydration pass.</summary>
    public IReadOnlyList<SecretVaultBinding> Bindings { get; init; } = [];

    /// <summary>Aliases of secrets whose vault entries could not be found or decrypted.</summary>
    public IReadOnlyList<string> MissingSecrets { get; init; } = [];

    /// <summary>
    /// Config field names containing legacy redacted placeholders that cannot
    /// be recovered from the vault and require operator re-entry.
    /// </summary>
    public IReadOnlyList<string> PlaceholderSecrets { get; init; } = [];

    /// <summary>
    /// Warning message when the current OS user differs from the vault creator's user.
    /// DPAPI CurrentUser scope binds secrets to the encrypting account — decryption
    /// under a different user account will fail.
    /// </summary>
    public string? DpapiScopeWarning { get; init; }

    /// <summary>
    /// Human-readable remediation steps for the operator.
    /// Populated when <see cref="AllResolved"/> is false.
    /// </summary>
    public IReadOnlyList<string> RemediationSteps { get; init; } = [];
}

// ── Vault diagnostics ─────────────────────────────────────────────────────────

/// <summary>
/// Metadata-only diagnostics for a DPAPI vault file.
/// Contains NO secret values, encrypted blobs, or credential material.
/// </summary>
public sealed class VaultDiagnostics
{
    public Guid DeploymentId { get; init; }

    /// <summary>True when the vault file exists on disk.</summary>
    public bool VaultFileExists { get; init; }

    /// <summary>Number of entries stored in the vault.</summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// List of stored secret aliases (not values).
    /// Example: ["domain.admin.password", "database.sys.password"]
    /// </summary>
    public IReadOnlyList<string> EntryAliases { get; init; } = [];

    /// <summary>OS user account that originally created the vault file.</summary>
    public string? VaultOwnerUser { get; init; }

    /// <summary>Machine name where the vault file was created.</summary>
    public string? VaultOwnerMachine { get; init; }

    /// <summary>The current OS user attempting vault access.</summary>
    public string CurrentUser { get; init; } = Environment.UserName;

    /// <summary>The current machine name.</summary>
    public string CurrentMachine { get; init; } = Environment.MachineName;

    /// <summary>
    /// True when DPAPI scope is likely compatible with the current execution context.
    /// False indicates a cross-user or cross-machine resume scenario that will fail decryption.
    /// </summary>
    public bool ScopeCompatible { get; init; }

    /// <summary>Warning message when DPAPI scope may be incompatible.</summary>
    public string? ScopeWarning { get; init; }

    /// <summary>UTC time the vault file was first created.</summary>
    public DateTimeOffset? VaultCreatedAt { get; init; }

    /// <summary>UTC time the vault file was last written.</summary>
    public DateTimeOffset? LastModifiedAt { get; init; }
}

/// <summary>
/// Structured diagnostics for a deployment's overall secret rehydration state.
/// Used by <see cref="WEDM.Application.Services.DeploymentRecoveryDiagnostics"/> and
/// operator tooling to surface recovery status without exposing credential values.
/// </summary>
public sealed class SecretRehydrationDiagnostics
{
    public Guid DeploymentId { get; init; }

    /// <summary>Total number of secret-holding configuration fields examined.</summary>
    public int TotalSecretFields { get; init; }

    /// <summary>Fields that contain vault reference sentinels (safe checkpoint state).</summary>
    public int VaultBoundFields { get; init; }

    /// <summary>Fields that contain runtime plaintext (not yet vaulted).</summary>
    public int PlainTextFields { get; init; }

    /// <summary>Fields that contain legacy "***REDACTED***" placeholders (unrecoverable).</summary>
    public int PlaceholderFields { get; init; }

    /// <summary>Fields that are empty / null (may be optional).</summary>
    public int EmptyFields { get; init; }

    /// <summary>True when all required secrets are vault-bound and resolvable.</summary>
    public bool ResumeReady { get; init; }

    /// <summary>DPAPI vault metadata for this deployment. Null when no vault file exists.</summary>
    public VaultDiagnostics? Vault { get; init; }

    /// <summary>
    /// Human-readable issues detected (no secret values, only field names and error types).
    /// </summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>Ordered remediation steps for the operator when <see cref="ResumeReady"/> is false.</summary>
    public IReadOnlyList<string> RemediationSteps { get; init; } = [];
}
