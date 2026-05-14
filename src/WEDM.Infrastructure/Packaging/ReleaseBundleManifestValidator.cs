using System.Security.Cryptography;
using WEDM.Domain.Models;

namespace WEDM.Infrastructure.Packaging;

/// <summary>Validates SHA-256 entries in a <see cref="WedmReleaseBundleManifest"/> against files on disk.</summary>
public static class ReleaseBundleManifestValidator
{
    public static bool TryValidate(string bundleRootDirectory, WedmReleaseBundleManifest manifest, out List<string> errors)
    {
        errors = new List<string>();
        if (string.IsNullOrWhiteSpace(bundleRootDirectory) || !Directory.Exists(bundleRootDirectory))
        {
            errors.Add("Bundle root directory is missing.");
            return false;
        }

        var root = Path.GetFullPath(bundleRootDirectory);

        foreach (var a in manifest.Artifacts)
        {
            var full = Path.GetFullPath(Path.Combine(root, a.RelativePath));
            var rel  = Path.GetRelativePath(root, full);
            if (rel.Equals("..", StringComparison.Ordinal) || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                errors.Add($"Artifact path escapes bundle root: {a.RelativePath}");
                continue;
            }

            if (!File.Exists(full))
            {
                errors.Add($"Missing artifact: {a.RelativePath}");
                continue;
            }

            var hash = ComputeSha256Hex(full);
            if (!string.Equals(hash, a.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Checksum mismatch for {a.RelativePath} (expected {a.Sha256}, actual {hash}).");
            }
        }

        return errors.Count == 0;
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes);
    }
}
