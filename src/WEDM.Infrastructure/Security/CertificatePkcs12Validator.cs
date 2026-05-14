using System.Security.Cryptography.X509Certificates;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Security;

public sealed class CertificatePkcs12Validator : ICertificateMaterialValidator
{
    public CertificateValidationResult ValidateIdentityKeystore(SslCertificateConfiguration ssl)
    {
        if (string.IsNullOrWhiteSpace(ssl.IdentityKeystorePath))
            return new CertificateValidationResult { Success = true, Message = "Identity keystore not configured." };

        var ext = Path.GetExtension(ssl.IdentityKeystorePath).ToLowerInvariant();
        if (ext is ".jks" or ".ks")
        {
            return new CertificateValidationResult
            {
                Success = false,
                Message = "JKS keystores are not validated in-process. Convert to PKCS#12 (.p12/.pfx) or validate with keytool."
            };
        }

        var path = Path.GetFullPath(ssl.IdentityKeystorePath);
        if (!File.Exists(path))
            return new CertificateValidationResult { Success = false, Message = $"Keystore file not found: {path}" };

        try
        {
            using var cert = new X509Certificate2(
                path,
                ssl.IdentityKeystorePassword ?? string.Empty,
                X509KeyStorageFlags.EphemeralKeySet);

            return new CertificateValidationResult
            {
                Success  = true,
                Message  = "PKCS#12 identity keystore loaded successfully.",
                Subject  = cert.Subject,
                NotAfter = new DateTimeOffset(cert.NotAfter)
            };
        }
        catch (Exception ex)
        {
            return new CertificateValidationResult
            {
                Success = false,
                Message = $"PKCS#12 load failed (wrong password or corrupt file): {ex.GetType().Name}"
            };
        }
    }
}
