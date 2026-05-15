using FluentAssertions;
using WEDM.Infrastructure.Security;
using Xunit;

namespace WEDM.Engine.Tests.Security;

/// <summary>
/// Integration tests for SecretRedactor.Redact() and SecretRedactor.FindLeaks().
/// These tests exercise the compiled regex rules end-to-end and verify that
/// all secret patterns covered in the recent audit pass are correctly handled.
/// </summary>
public sealed class SecretRedactorIntegrationTests
{
    // ── Redact tests ──────────────────────────────────────────────────────────

    [Fact]
    public void Redact_PlaintextPassword_IsRedacted()
    {
        const string input = "password=secret123";

        var result = SecretRedactor.Redact(input);

        result.Should().Contain("***REDACTED***");
        result.Should().NotContain("secret123");
    }

    [Fact]
    public void Redact_Base64PasswordInFromBase64String_IsRedacted()
    {
        // ≥20-char base64 segment (threshold in SecretRedactor) after PowerShell [Convert]:: prefix
        const string input = "[Convert]::FromBase64String('YWRtaW5wYXNzMTIzNDU2Nzg5')";

        var result = SecretRedactor.Redact(input);

        result.Should().Contain("***REDACTED***");
        result.Should().NotContain("YWRtaW5wYXNzMTIzNDU2Nzg5");
    }

    [Fact]
    public void Redact_BearerToken_IsRedacted()
    {
        const string input = "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.test";

        var result = SecretRedactor.Redact(input);

        result.Should().Contain("***REDACTED***");
        result.Should().NotContain("eyJhbGciOiJIUzI1NiJ9.test");
    }

    [Fact]
    public void Redact_BasicAuthInUrl_IsRedacted()
    {
        const string input = "https://user:supersecret@oracle.example.com/download";

        var result = SecretRedactor.Redact(input);

        // Password segment must be redacted
        result.Should().Contain("***REDACTED***");
        result.Should().NotContain("supersecret");

        // Username and host must be preserved
        result.Should().Contain("user:");
        result.Should().Contain("oracle.example.com");
    }

    [Fact]
    public void Redact_PemPrivateKey_IsRedacted()
    {
        const string input =
            "-----BEGIN RSA PRIVATE KEY-----\n" +
            "MIIEowIBAAKCAQEA0Z3VS5JJcds3xHn/ygWep4PAtEsHAEz5yDKRaI0lsXYL0H9A\n" +
            "-----END RSA PRIVATE KEY-----";

        var result = SecretRedactor.Redact(input);

        result.Should().Contain("***PRIVATE-KEY-REDACTED***");
        result.Should().NotContain("MIIEowIBAAKCAQEA");
    }

    [Fact]
    public void Redact_WedmAdminPass_IsRedacted()
    {
        const string input = "$env:WEDM_ADMIN_PASS = 'hunter2'";

        var result = SecretRedactor.Redact(input);

        result.Should().Contain("***REDACTED***");
        result.Should().NotContain("hunter2");
    }

    [Fact]
    public void FindLeaks_AfterRedact_ReturnsEmpty()
    {
        // Construct text that exercises multiple redaction rules
        var rawText =
            "password=supersecret\n" +
            "$env:WEDM_ADMIN_PASS = 'hunter2'\n" +
            "FromBase64String('YWRtaW5wYXNzMTIzNDU2Nzg=')";

        var redacted = SecretRedactor.Redact(rawText);
        var leaks    = SecretRedactor.FindLeaks(redacted);

        leaks.Should().BeEmpty(because: "all secrets should have been removed by Redact()");
    }

    [Fact]
    public void FindLeaks_UnredactedPassword_FindsLeak()
    {
        const string text = "password = mypassword123";

        var leaks = SecretRedactor.FindLeaks(text);

        leaks.Should().Contain("password-field");
    }

    [Fact]
    public void FindLeaks_Base64Password_FindsLeak()
    {
        // Provide a FromBase64String call with a 20+ character base64 value
        const string text = "FromBase64String('YWRtaW5wYXNzMTIzNDU2Nzg=')";

        var leaks = SecretRedactor.FindLeaks(text);

        leaks.Should().Contain("base64-password");
    }

    [Fact]
    public void Redact_NullOrEmpty_ReturnsEmpty()
    {
        SecretRedactor.Redact(null).Should().Be(string.Empty);
        SecretRedactor.Redact(string.Empty).Should().Be(string.Empty);
    }
}
