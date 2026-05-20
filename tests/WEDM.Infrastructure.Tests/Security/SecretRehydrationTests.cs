using System.Text.Json;
using FluentAssertions;
using WEDM.Domain.Models;
using WEDM.Infrastructure.Security;
using Xunit;

// Tests run on Windows only — DPAPI requires a real OS user context.
// Tests that touch DpapiFileSecretVault are marked with the "dpapi" trait
// so CI on non-Windows agents can skip them via: dotnet test --filter Trait!=dpapi

namespace WEDM.Infrastructure.Tests.Security;

// ═══════════════════════════════════════════════════════════════════════════════
// SecretReference sentinel helpers
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SecretReferenceSentinelTests
{
    [Fact]
    public void ToSentinel_produces_correct_format()
    {
        var s = SecretReference.ToSentinel("domain.admin.password");
        s.Should().Be("__WEDM_VAULT_REF:domain.admin.password__");
    }

    [Fact]
    public void IsSentinel_detects_valid_sentinel()
    {
        SecretReference.IsSentinel("__WEDM_VAULT_REF:domain.admin.password__")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("***REDACTED***")]
    [InlineData("plaintext")]
    [InlineData("__WEDM_VAULT_REF:__")]   // empty alias — too short
    [InlineData("__WEDM_VAULT_REF:x")]    // missing suffix
    [InlineData("WEDM_VAULT_REF:x__")]    // missing prefix
    public void IsSentinel_rejects_non_sentinels(string? value)
    {
        SecretReference.IsSentinel(value).Should().BeFalse();
    }

    [Fact]
    public void ExtractAlias_returns_alias_from_valid_sentinel()
    {
        var alias = SecretReference.ExtractAlias("__WEDM_VAULT_REF:database.sys.password__");
        alias.Should().Be("database.sys.password");
    }

    [Fact]
    public void ExtractAlias_returns_null_for_non_sentinel()
    {
        SecretReference.ExtractAlias("plaintext").Should().BeNull();
        SecretReference.ExtractAlias(null).Should().BeNull();
    }

    [Theory]
    [InlineData("***REDACTED***")]
    [InlineData("***")]
    [InlineData("REDACTED")]
    [InlineData("***PASSWORD***")]
    public void IsLegacyPlaceholder_detects_all_legacy_forms(string value)
    {
        SecretReference.IsLegacyPlaceholder(value).Should().BeTrue();
    }

    [Theory]
    [InlineData("realpassword")]
    [InlineData("__WEDM_VAULT_REF:domain.admin.password__")]
    [InlineData(null)]
    [InlineData("")]
    public void IsLegacyPlaceholder_ignores_non_legacy(string? value)
    {
        SecretReference.IsLegacyPlaceholder(value).Should().BeFalse();
    }

    [Fact]
    public void NeedsResolution_true_for_sentinel_and_placeholder()
    {
        SecretReference.NeedsResolution("__WEDM_VAULT_REF:domain.admin.password__").Should().BeTrue();
        SecretReference.NeedsResolution("***REDACTED***").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("realvalue")]
    public void NeedsResolution_false_for_empty_or_plaintext(string? value)
    {
        SecretReference.NeedsResolution(value).Should().BeFalse();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// InMemorySecretVault — test double that avoids DPAPI
// ═══════════════════════════════════════════════════════════════════════════════

file sealed class InMemorySecretVault : WEDM.Domain.Interfaces.ILocalSecretVault
{
    private readonly Dictionary<string, Dictionary<string, string>> _store = new();
    private readonly string _ownerUser;
    private readonly bool _simulateScopeMismatch;

    public InMemorySecretVault(
        string? ownerUser = null,
        bool simulateScopeMismatch = false)
    {
        _ownerUser            = ownerUser ?? Environment.UserName;
        _simulateScopeMismatch = simulateScopeMismatch;
    }

    public void Save(Guid deploymentId, string secretName, string plaintext)
    {
        if (!_store.ContainsKey(deploymentId.ToString()))
            _store[deploymentId.ToString()] = new();
        _store[deploymentId.ToString()][secretName] = plaintext;
    }

    public string? TryLoad(Guid deploymentId, string secretName)
    {
        if (_simulateScopeMismatch) return null; // simulate DPAPI user-scope failure
        return _store.TryGetValue(deploymentId.ToString(), out var map)
            && map.TryGetValue(secretName, out var val) ? val : null;
    }

    public bool Exists(Guid deploymentId, string secretName)
        => _store.TryGetValue(deploymentId.ToString(), out var map)
           && map.ContainsKey(secretName);

    public IReadOnlyList<string> ListKeys(Guid deploymentId)
        => _store.TryGetValue(deploymentId.ToString(), out var map)
            ? map.Keys.ToList()
            : [];

    public void Delete(Guid deploymentId, string secretName)
    {
        if (_store.TryGetValue(deploymentId.ToString(), out var map))
            map.Remove(secretName);
    }

    public VaultDiagnostics GetDiagnostics(Guid deploymentId)
    {
        var exists    = _store.ContainsKey(deploymentId.ToString());
        var currentUser = Environment.UserName;
        var compatible  = !_simulateScopeMismatch;
        return new VaultDiagnostics
        {
            DeploymentId    = deploymentId,
            VaultFileExists  = exists,
            EntryCount       = exists ? _store[deploymentId.ToString()].Count : 0,
            EntryAliases     = exists ? _store[deploymentId.ToString()].Keys.ToList() : [],
            VaultOwnerUser   = _ownerUser,
            CurrentUser      = currentUser,
            ScopeCompatible  = compatible,
            ScopeWarning     = _simulateScopeMismatch
                ? $"Vault created by '{_ownerUser}' but current user is '{currentUser}'."
                : null,
        };
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// PersistAndBind tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SecretRehydrationService_PersistAndBindTests
{
    private static DeploymentConfiguration ConfigWithSecrets() => new()
    {
        Domain   = { AdminPassword    = "Admin@123!" },
        Database = { SysPassword      = "Sys@456",
                     SchemaPassword   = "Schema@789" },
        Security = { SslCertificates  = { IdentityKeystorePassword = "Keystore@000" } }
    };

    [Fact]
    public void PersistAndBind_replaces_plaintext_with_sentinels()
    {
        var vault   = new InMemorySecretVault();
        var svc     = new SecretRehydrationService(vault);
        var config  = ConfigWithSecrets();
        var depId   = Guid.NewGuid();

        svc.PersistAndBind(config, depId);

        SecretReference.IsSentinel(config.Domain.AdminPassword).Should().BeTrue();
        SecretReference.IsSentinel(config.Database.SysPassword).Should().BeTrue();
        SecretReference.IsSentinel(config.Database.SchemaPassword).Should().BeTrue();
        SecretReference.IsSentinel(config.Security.SslCertificates.IdentityKeystorePassword).Should().BeTrue();
    }

    [Fact]
    public void PersistAndBind_saves_all_secrets_to_vault()
    {
        var vault   = new InMemorySecretVault();
        var svc     = new SecretRehydrationService(vault);
        var config  = ConfigWithSecrets();
        var depId   = Guid.NewGuid();

        svc.PersistAndBind(config, depId);

        vault.Exists(depId, "domain.admin.password").Should().BeTrue();
        vault.Exists(depId, "database.sys.password").Should().BeTrue();
        vault.Exists(depId, "database.schema.password").Should().BeTrue();
        vault.Exists(depId, "security.ssl.identity.keystorepassword").Should().BeTrue();
    }

    [Fact]
    public void PersistAndBind_returns_resolved_bindings()
    {
        var vault    = new InMemorySecretVault();
        var svc      = new SecretRehydrationService(vault);
        var config   = ConfigWithSecrets();
        var depId    = Guid.NewGuid();

        var bindings = svc.PersistAndBind(config, depId);

        bindings.Should().NotBeEmpty();
        bindings.Should().OnlyContain(b => b.IsResolved);
        bindings.Should().OnlyContain(b => b.Source == SecretResolutionSource.DpapiVault);
    }

    [Fact]
    public void PersistAndBind_skips_empty_fields()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = "Admin@123!" }
            // Database and SSL passwords left empty
        };
        var depId = Guid.NewGuid();

        svc.PersistAndBind(config, depId);

        vault.Exists(depId, "domain.admin.password").Should().BeTrue();
        vault.Exists(depId, "database.sys.password").Should().BeFalse();
        vault.Exists(depId, "database.schema.password").Should().BeFalse();
    }

    [Fact]
    public void PersistAndBind_does_not_rewrap_existing_sentinels()
    {
        var vault   = new InMemorySecretVault();
        var svc     = new SecretRehydrationService(vault);
        var depId   = Guid.NewGuid();

        // Pre-populate vault and set sentinel (simulating second checkpoint)
        vault.Save(depId, "domain.admin.password", "Admin@123!");
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        var bindings = svc.PersistAndBind(config, depId);

        // Sentinel should be preserved unchanged
        config.Domain.AdminPassword.Should().Be(SecretReference.ToSentinel("domain.admin.password"));
        bindings.Should().Contain(b => b.Reference.Alias == "domain.admin.password" && b.IsResolved);
    }

    [Fact]
    public void PersistAndBind_marks_legacy_placeholders_as_unresolved()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = "***REDACTED***" }
        };
        var depId = Guid.NewGuid();

        var bindings = svc.PersistAndBind(config, depId);

        var adminBinding = bindings.FirstOrDefault(b => b.Reference.Alias == "domain.admin.password");
        adminBinding.Should().NotBeNull();
        adminBinding!.IsResolved.Should().BeFalse();
        adminBinding.Source.Should().Be(SecretResolutionSource.LegacyPlaceholder);
    }

    [Fact]
    public void PersistAndBind_plaintext_not_in_sentinel_config()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var config = ConfigWithSecrets();
        var depId  = Guid.NewGuid();

        svc.PersistAndBind(config, depId);

        // Verify no plaintext secrets remain in the config after binding
        config.Domain.AdminPassword.Should().NotContain("Admin@123!");
        config.Database.SysPassword.Should().NotContain("Sys@456");
        config.Database.SchemaPassword.Should().NotContain("Schema@789");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Rehydrate tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SecretRehydrationService_RehydrateTests
{
    [Fact]
    public void Rehydrate_restores_all_secrets_from_vault()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        // Simulate checkpoint state: sentinels in config, values in vault
        vault.Save(depId, "domain.admin.password", "Admin@123!");
        vault.Save(depId, "database.sys.password", "Sys@456");
        vault.Save(depId, "database.schema.password", "Schema@789");

        var config = new DeploymentConfiguration
        {
            Domain   = { AdminPassword  = SecretReference.ToSentinel("domain.admin.password") },
            Database = { SysPassword    = SecretReference.ToSentinel("database.sys.password"),
                         SchemaPassword = SecretReference.ToSentinel("database.schema.password") },
        };

        var result = svc.Rehydrate(config, depId);

        result.AllResolved.Should().BeTrue();
        config.Domain.AdminPassword.Should().Be("Admin@123!");
        config.Database.SysPassword.Should().Be("Sys@456");
        config.Database.SchemaPassword.Should().Be("Schema@789");
    }

    [Fact]
    public void Rehydrate_fails_when_vault_entry_missing()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        // No vault entries — config has sentinels pointing to nothing
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        var result = svc.Rehydrate(config, depId);

        result.AllResolved.Should().BeFalse();
        result.MissingSecrets.Should().Contain("domain.admin.password");
        result.RemediationSteps.Should().NotBeEmpty();
    }

    [Fact]
    public void Rehydrate_fails_for_legacy_placeholder()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = "***REDACTED***" }
        };

        var result = svc.Rehydrate(config, depId);

        result.AllResolved.Should().BeFalse();
        result.PlaceholderSecrets.Should().Contain("domain.admin.password");
        result.RemediationSteps.Should().NotBeEmpty();
    }

    [Fact]
    public void Rehydrate_succeeds_with_empty_optional_fields()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        vault.Save(depId, "domain.admin.password", "Admin@123!");
        var config = new DeploymentConfiguration
        {
            Domain   = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") },
            Database = { SysPassword   = string.Empty, SchemaPassword = string.Empty },
        };

        var result = svc.Rehydrate(config, depId);

        result.AllResolved.Should().BeTrue();
        config.Domain.AdminPassword.Should().Be("Admin@123!");
    }

    [Fact]
    public void Rehydrate_detects_dpapi_scope_mismatch()
    {
        var vault = new InMemorySecretVault(
            ownerUser: "DOMAIN\\other-user",
            simulateScopeMismatch: true);
        var svc   = new SecretRehydrationService(vault);
        var depId = Guid.NewGuid();

        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        var result = svc.Rehydrate(config, depId);

        result.AllResolved.Should().BeFalse();
        result.DpapiScopeWarning.Should().NotBeNullOrEmpty();
        result.MissingSecrets.Should().Contain("domain.admin.password");
    }

    [Fact]
    public void Rehydrate_mutates_config_when_resolved()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        vault.Save(depId, "domain.admin.password", "RealPassword!");
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        var originalSentinel = config.Domain.AdminPassword;
        svc.Rehydrate(config, depId);

        config.Domain.AdminPassword.Should().NotBe(originalSentinel);
        config.Domain.AdminPassword.Should().Be("RealPassword!");
    }

    [Fact]
    public void Rehydrate_does_not_mutate_config_when_failed()
    {
        var vault  = new InMemorySecretVault(); // vault is empty
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        var sentinel = SecretReference.ToSentinel("domain.admin.password");
        var config   = new DeploymentConfiguration
        {
            Domain = { AdminPassword = sentinel }
        };

        svc.Rehydrate(config, depId);

        // Sentinel should remain since vault entry was missing — no partial mutation
        config.Domain.AdminPassword.Should().Be(sentinel);
    }

    [Fact]
    public void Rehydrate_handles_plaintext_fields_without_touching_them()
    {
        // Plaintext fields should be treated as already-resolved (upgrade scenario)
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = "UnvaultedPlaintext" }
        };

        var result = svc.Rehydrate(config, depId);

        result.AllResolved.Should().BeTrue();
        config.Domain.AdminPassword.Should().Be("UnvaultedPlaintext");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ValidateForResume tests (dry-run — does not mutate config)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SecretRehydrationService_ValidateForResumeTests
{
    [Fact]
    public void ValidateForResume_passes_when_all_secrets_vaulted()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        vault.Save(depId, "domain.admin.password", "Admin@123!");
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        var result = svc.ValidateForResume(config, depId);

        result.AllResolved.Should().BeTrue();
    }

    [Fact]
    public void ValidateForResume_does_not_modify_config()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        vault.Save(depId, "domain.admin.password", "Admin@123!");
        var sentinel = SecretReference.ToSentinel("domain.admin.password");
        var config   = new DeploymentConfiguration
        {
            Domain = { AdminPassword = sentinel }
        };

        svc.ValidateForResume(config, depId);

        // Config must be unchanged — validation is a dry-run
        config.Domain.AdminPassword.Should().Be(sentinel);
    }

    [Fact]
    public void ValidateForResume_fails_for_legacy_placeholders()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = "***REDACTED***" }
        };

        var result = svc.ValidateForResume(config, depId);

        result.AllResolved.Should().BeFalse();
        result.PlaceholderSecrets.Should().Contain("domain.admin.password");
        result.RemediationSteps.Should().NotBeEmpty();
    }

    [Fact]
    public void ValidateForResume_fails_for_missing_vault_entries()
    {
        var vault  = new InMemorySecretVault(); // vault empty
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        var result = svc.ValidateForResume(config, depId);

        result.AllResolved.Should().BeFalse();
        result.MissingSecrets.Should().Contain("domain.admin.password");
    }

    [Fact]
    public void ValidateForResume_emits_scope_warning_on_mismatch()
    {
        var vault = new InMemorySecretVault(
            ownerUser: "OtherUser",
            simulateScopeMismatch: true);
        var svc   = new SecretRehydrationService(vault);
        var depId = Guid.NewGuid();

        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        var result = svc.ValidateForResume(config, depId);

        result.AllResolved.Should().BeFalse();
        result.DpapiScopeWarning.Should().NotBeNullOrEmpty();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// HasLegacyPlaceholders / HasVaultReferences
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SecretRehydrationService_InspectionTests
{
    [Fact]
    public void HasLegacyPlaceholders_true_when_any_field_is_redacted()
    {
        var svc    = new SecretRehydrationService(new InMemorySecretVault());
        var config = new DeploymentConfiguration
        {
            Domain   = { AdminPassword = "***REDACTED***" },
            Database = { SysPassword   = "realvalue" }
        };

        svc.HasLegacyPlaceholders(config).Should().BeTrue();
    }

    [Fact]
    public void HasLegacyPlaceholders_false_when_no_redacted_fields()
    {
        var svc    = new SecretRehydrationService(new InMemorySecretVault());
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = "realpassword" }
        };

        svc.HasLegacyPlaceholders(config).Should().BeFalse();
    }

    [Fact]
    public void HasVaultReferences_true_when_any_field_has_sentinel()
    {
        var svc    = new SecretRehydrationService(new InMemorySecretVault());
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        svc.HasVaultReferences(config).Should().BeTrue();
    }

    [Fact]
    public void HasVaultReferences_false_when_no_sentinels()
    {
        var svc    = new SecretRehydrationService(new InMemorySecretVault());
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = "plaintext" }
        };

        svc.HasVaultReferences(config).Should().BeFalse();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// GetDiagnostics
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SecretRehydrationService_DiagnosticsTests
{
    [Fact]
    public void Diagnostics_reflects_vault_bound_fields()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        vault.Save(depId, "domain.admin.password", "password");
        var config = new DeploymentConfiguration
        {
            Domain   = { AdminPassword  = SecretReference.ToSentinel("domain.admin.password") },
            Database = { SysPassword    = "plaintext" },
        };

        var diag = svc.GetDiagnostics(config, depId);

        diag.VaultBoundFields.Should().Be(1);
        diag.PlainTextFields.Should().Be(1);
        diag.PlaceholderFields.Should().Be(0);
    }

    [Fact]
    public void Diagnostics_detects_placeholder_fields()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var config = new DeploymentConfiguration
        {
            Domain   = { AdminPassword = "***REDACTED***" },
            Database = { SysPassword   = "***" },
        };

        var diag = svc.GetDiagnostics(config, Guid.NewGuid());

        diag.PlaceholderFields.Should().Be(2);
        diag.ResumeReady.Should().BeFalse();
        diag.Issues.Should().NotBeEmpty();
    }

    [Fact]
    public void Diagnostics_resume_ready_when_all_vaulted()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        vault.Save(depId, "domain.admin.password", "pass");
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        var diag = svc.GetDiagnostics(config, depId);

        diag.ResumeReady.Should().BeTrue();
        diag.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Diagnostics_includes_vault_metadata()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        vault.Save(depId, "domain.admin.password", "pass");
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        var diag = svc.GetDiagnostics(config, depId);

        diag.Vault.Should().NotBeNull();
        diag.Vault!.VaultFileExists.Should().BeTrue();
        diag.Vault.EntryCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Diagnostics_scope_mismatch_sets_resume_not_ready()
    {
        var vault = new InMemorySecretVault(simulateScopeMismatch: true);
        var svc   = new SecretRehydrationService(vault);
        var depId = Guid.NewGuid();

        vault.Save(depId, "domain.admin.password", "pass");
        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = SecretReference.ToSentinel("domain.admin.password") }
        };

        var diag = svc.GetDiagnostics(config, depId);

        diag.ResumeReady.Should().BeFalse();
        diag.Vault!.ScopeCompatible.Should().BeFalse();
        diag.Issues.Should().NotBeEmpty();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Full checkpoint round-trip — PersistAndBind → Rehydrate
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SecretRehydrationService_RoundTripTests
{
    [Fact]
    public void Checkpoint_save_then_resume_round_trip_restores_all_secrets()
    {
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        // Runtime config — as it exists in memory during deployment
        var liveConfig = new DeploymentConfiguration
        {
            Domain   = { AdminPassword  = "WebLogic1!" },
            Database = { SysPassword    = "SysPass99",
                         SchemaPassword = "SchemaPass77" },
            Security = { SslCertificates = { IdentityKeystorePassword = "KeystorePass" } }
        };

        // ── Simulate checkpoint save ──────────────────────────────────────────
        // Clone the config (as JsonDeploymentSessionStore does)
        var json        = JsonSerializer.Serialize(liveConfig);
        var checkpointConfig = JsonSerializer.Deserialize<DeploymentConfiguration>(json)!;

        // PersistAndBind replaces plaintext with sentinels in the clone
        svc.PersistAndBind(checkpointConfig, depId);

        // The checkpoint config should have no plaintext
        checkpointConfig.Domain.AdminPassword.Should().StartWith("__WEDM_VAULT_REF:");
        checkpointConfig.Database.SysPassword.Should().StartWith("__WEDM_VAULT_REF:");
        checkpointConfig.Database.SchemaPassword.Should().StartWith("__WEDM_VAULT_REF:");
        checkpointConfig.Security.SslCertificates.IdentityKeystorePassword.Should().StartWith("__WEDM_VAULT_REF:");

        // The live config should be UNCHANGED
        liveConfig.Domain.AdminPassword.Should().Be("WebLogic1!");

        // ── Simulate crash + reload ───────────────────────────────────────────
        // Deserialize the checkpoint (as if loaded from disk after crash)
        var resumeJson   = JsonSerializer.Serialize(checkpointConfig);
        var resumeConfig = JsonSerializer.Deserialize<DeploymentConfiguration>(resumeJson)!;

        // ── Simulate resume validation ────────────────────────────────────────
        var validation = svc.ValidateForResume(resumeConfig, depId);
        validation.AllResolved.Should().BeTrue("all vault entries should be resolvable");

        // ── Simulate rehydration ──────────────────────────────────────────────
        var rehydration = svc.Rehydrate(resumeConfig, depId);
        rehydration.AllResolved.Should().BeTrue();

        resumeConfig.Domain.AdminPassword.Should().Be("WebLogic1!");
        resumeConfig.Database.SysPassword.Should().Be("SysPass99");
        resumeConfig.Database.SchemaPassword.Should().Be("SchemaPass77");
        resumeConfig.Security.SslCertificates.IdentityKeystorePassword.Should().Be("KeystorePass");
    }

    [Fact]
    public void Backward_compatible_checkpoint_with_legacy_placeholders_blocks_resume()
    {
        // Old checkpoint persisted via DeploymentConfigurationSanitizer.RedactSecrets()
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        // Simulate loaded checkpoint from an OLD WEDM build
        var oldConfig = new DeploymentConfiguration
        {
            Domain   = { AdminPassword  = "***REDACTED***" },
            Database = { SysPassword    = "***REDACTED***",
                         SchemaPassword = "***REDACTED***" },
        };

        var validation = svc.ValidateForResume(oldConfig, depId);

        // Must block resume with actionable remediation
        validation.AllResolved.Should().BeFalse();
        validation.PlaceholderSecrets.Should().HaveCount(3);
        validation.RemediationSteps.Should().NotBeEmpty();
        validation.RemediationSteps.Should().Contain(r => r.Contains("legacy") || r.Contains("re-enter") || r.Contains("re-run"));
    }

    [Fact]
    public void Multiple_checkpoints_do_not_double_vault_sentinels()
    {
        // Second checkpoint call on already-vaulted config should be idempotent
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        var config = new DeploymentConfiguration
        {
            Domain = { AdminPassword = "Admin@123!" }
        };

        // First checkpoint
        svc.PersistAndBind(config, depId);
        var sentinel1 = config.Domain.AdminPassword;

        // Second checkpoint (config already has sentinel)
        svc.PersistAndBind(config, depId);
        var sentinel2 = config.Domain.AdminPassword;

        // Sentinel must not be re-wrapped
        sentinel1.Should().Be(sentinel2);
        SecretReference.IsSentinel(sentinel2).Should().BeTrue();
        SecretReference.ExtractAlias(sentinel2).Should().Be("domain.admin.password");
    }

    [Fact]
    public void Rollback_after_resume_uses_rehydrated_secrets()
    {
        // Verify that after rehydration, the config secrets are available for
        // rollback operations (WLST auth, RCU cleanup, SSL cleanup).
        var vault  = new InMemorySecretVault();
        var svc    = new SecretRehydrationService(vault);
        var depId  = Guid.NewGuid();

        vault.Save(depId, "domain.admin.password", "RollbackPass!");
        vault.Save(depId, "database.schema.password", "RcuPass!");

        var config = new DeploymentConfiguration
        {
            Domain   = { AdminPassword  = SecretReference.ToSentinel("domain.admin.password") },
            Database = { SchemaPassword = SecretReference.ToSentinel("database.schema.password") },
        };

        svc.Rehydrate(config, depId);

        // After rehydration, secrets are available for rollback steps
        config.Domain.AdminPassword.Should().Be("RollbackPass!");
        config.Database.SchemaPassword.Should().Be("RcuPass!");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// InMemoryVault unit tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class InMemorySecretVaultTests
{
    [Fact]
    public void Save_and_TryLoad_round_trip()
    {
        var vault  = new InMemorySecretVault();
        var depId  = Guid.NewGuid();

        vault.Save(depId, "test.secret", "my-value");
        vault.TryLoad(depId, "test.secret").Should().Be("my-value");
    }

    [Fact]
    public void TryLoad_returns_null_for_missing_key()
    {
        var vault = new InMemorySecretVault();
        vault.TryLoad(Guid.NewGuid(), "nonexistent").Should().BeNull();
    }

    [Fact]
    public void Exists_returns_false_before_save()
    {
        var vault = new InMemorySecretVault();
        vault.Exists(Guid.NewGuid(), "key").Should().BeFalse();
    }

    [Fact]
    public void Delete_removes_entry()
    {
        var vault = new InMemorySecretVault();
        var depId = Guid.NewGuid();

        vault.Save(depId, "k", "v");
        vault.Exists(depId, "k").Should().BeTrue();

        vault.Delete(depId, "k");
        vault.Exists(depId, "k").Should().BeFalse();
    }

    [Fact]
    public void ListKeys_returns_all_aliases()
    {
        var vault = new InMemorySecretVault();
        var depId = Guid.NewGuid();

        vault.Save(depId, "a", "1");
        vault.Save(depId, "b", "2");
        vault.Save(depId, "c", "3");

        vault.ListKeys(depId).Should().BeEquivalentTo(["a", "b", "c"]);
    }

    [Fact]
    public void Scope_mismatch_simulation_returns_null_on_TryLoad()
    {
        var vault = new InMemorySecretVault(
            ownerUser: "other-user",
            simulateScopeMismatch: true);
        var depId = Guid.NewGuid();

        vault.Save(depId, "key", "value");
        vault.TryLoad(depId, "key").Should().BeNull("simulated DPAPI scope mismatch");
    }

    [Fact]
    public void Diagnostics_reports_scope_mismatch_warning()
    {
        var vault = new InMemorySecretVault(
            ownerUser: "other-user",
            simulateScopeMismatch: true);
        var depId = Guid.NewGuid();

        vault.Save(depId, "key", "value");
        var diag = vault.GetDiagnostics(depId);

        diag.ScopeCompatible.Should().BeFalse();
        diag.ScopeWarning.Should().NotBeNullOrEmpty();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// DeploymentSessionState secret reference serialization
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DeploymentSessionState_SecretReferenceTests
{
    [Fact]
    public void SecretReferences_serializes_and_deserializes_correctly()
    {
        var sessionId  = Guid.NewGuid();
        var deployId   = Guid.NewGuid();
        var state      = new DeploymentSessionState
        {
            SessionId       = sessionId,
            ConfigurationId = deployId,
            SecretReferences = [
                new SecretReference
                {
                    Alias        = "domain.admin.password",
                    DeploymentId = deployId,
                    Kind         = SecretKind.AdminPassword,
                    Description  = "WebLogic admin password",
                }
            ]
        };

        var json      = JsonSerializer.Serialize(state);
        var restored  = JsonSerializer.Deserialize<DeploymentSessionState>(json)!;

        restored.SecretReferences.Should().HaveCount(1);
        restored.SecretReferences[0].Alias.Should().Be("domain.admin.password");
        restored.SecretReferences[0].Kind.Should().Be(SecretKind.AdminPassword);
    }

    [Fact]
    public void SecretReferences_defaults_to_empty_on_old_checkpoint()
    {
        // Old checkpoint has no "secretReferences" key
        var json  = """{"schemaVersion":1,"sessionId":"00000000000000000000000000000001","configurationId":"00000000000000000000000000000002"}""";
        var state = JsonSerializer.Deserialize<DeploymentSessionState>(json);

        state.Should().NotBeNull();
        state!.SecretReferences.Should().BeEmpty("backward compat: old checkpoints have no secretReferences");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// DPAPI vault integration tests (require Windows + user context)
// ═══════════════════════════════════════════════════════════════════════════════

[Trait("category", "dpapi")]
public sealed class DpapiFileSecretVaultTests : IDisposable
{
    private readonly string _vaultDir = Path.Combine(Path.GetTempPath(), "wedm-vault-it", Guid.NewGuid().ToString("N"));
    private readonly DpapiFileSecretVault _vault;

    public DpapiFileSecretVaultTests()
    {
        Directory.CreateDirectory(_vaultDir);
        _vault = new DpapiFileSecretVault(_vaultDir);
    }

    [Fact]
    public void Save_and_TryLoad_round_trip_with_dpapi()
    {
        var depId = Guid.NewGuid();
        _vault.Save(depId, "test.secret", "Hello, DPAPI!");
        _vault.TryLoad(depId, "test.secret").Should().Be("Hello, DPAPI!");
    }

    [Fact]
    public void TryLoad_returns_null_for_missing_key()
    {
        _vault.TryLoad(Guid.NewGuid(), "missing").Should().BeNull();
    }

    [Fact]
    public void Exists_detects_saved_entry()
    {
        var depId = Guid.NewGuid();
        _vault.Exists(depId, "k").Should().BeFalse();
        _vault.Save(depId, "k", "v");
        _vault.Exists(depId, "k").Should().BeTrue();
    }

    [Fact]
    public void ListKeys_excludes_meta_key()
    {
        var depId = Guid.NewGuid();
        _vault.Save(depId, "a", "1");
        _vault.Save(depId, "b", "2");

        var keys = _vault.ListKeys(depId);
        keys.Should().BeEquivalentTo(["a", "b"]);
        keys.Should().NotContain("_meta_");
    }

    [Fact]
    public void Delete_removes_single_entry()
    {
        var depId = Guid.NewGuid();
        _vault.Save(depId, "a", "1");
        _vault.Save(depId, "b", "2");
        _vault.Delete(depId, "a");

        _vault.Exists(depId, "a").Should().BeFalse();
        _vault.Exists(depId, "b").Should().BeTrue();
    }

    [Fact]
    public void GetDiagnostics_reflects_current_user_scope()
    {
        var depId = Guid.NewGuid();
        _vault.Save(depId, "test", "value");

        var diag = _vault.GetDiagnostics(depId);

        diag.VaultFileExists.Should().BeTrue();
        diag.ScopeCompatible.Should().BeTrue("vault was created by the same user");
        diag.ScopeWarning.Should().BeNull();
        diag.VaultOwnerUser.Should().Be(Environment.UserName);
        diag.CurrentUser.Should().Be(Environment.UserName);
        diag.EntryCount.Should().Be(1);
    }

    [Fact]
    public void GetDiagnostics_returns_no_file_info_when_vault_missing()
    {
        var diag = _vault.GetDiagnostics(Guid.NewGuid());
        diag.VaultFileExists.Should().BeFalse();
        diag.ScopeCompatible.Should().BeFalse();
        diag.ScopeWarning.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Vault_metadata_persists_owner_and_timestamps()
    {
        var depId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        _vault.Save(depId, "k", "v");
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var diag = _vault.GetDiagnostics(depId);

        diag.VaultCreatedAt.Should().NotBeNull();
        diag.VaultCreatedAt!.Value.Should().BeAfter(before).And.BeBefore(after);
        diag.VaultOwnerUser.Should().Be(Environment.UserName);
        diag.VaultOwnerMachine.Should().Be(Environment.MachineName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_vaultDir, recursive: true); } catch { }
    }
}

