using WEDM.Domain.Models;

namespace WEDM.Engine.Discovery;

/// <summary>Validates middleware and domain paths before a real discovery scan may run.</summary>
public static class DiscoveryEnvironmentValidator
{
    public static DiscoveryPathValidationResult Validate(string? middlewareHome, string? domainHome)
    {
        var errors = new List<string>();
        var mwError = string.Empty;
        var domainError = string.Empty;

        if (string.IsNullOrWhiteSpace(middlewareHome))
        {
            mwError = "Middleware Home is required.";
            errors.Add(mwError);
        }
        else if (!TryNormalizePath(middlewareHome, out var mwPath, out mwError))
        {
            errors.Add(mwError);
        }
        else if (!SafeDiscoveryIO.DirectoryExists(mwPath))
        {
            mwError = "Middleware Home not found.";
            errors.Add(mwError);
        }
        else if (!LooksLikeOracleMiddleware(mwPath))
        {
            mwError = "Invalid Oracle Middleware directory (expected wlserver, oracle_common, or registry.xml).";
            errors.Add(mwError);
        }

        if (string.IsNullOrWhiteSpace(domainHome))
        {
            domainError = "Domain Home is required.";
            errors.Add(domainError);
        }
        else if (!TryNormalizePath(domainHome, out var domainPath, out domainError))
        {
            errors.Add(domainError);
        }
        else if (!SafeDiscoveryIO.DirectoryExists(domainPath))
        {
            domainError = "Domain Home not found.";
            errors.Add(domainError);
        }
        else if (!LooksLikeWebLogicDomain(domainPath, out var domainStructureError))
        {
            domainError = domainStructureError;
            errors.Add(domainError);
        }

        return new DiscoveryPathValidationResult
        {
            IsValid             = errors.Count == 0,
            MiddlewareHomeError = mwError,
            DomainHomeError     = domainError,
            Errors              = errors,
        };
    }

    private static bool TryNormalizePath(string path, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error    = string.Empty;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
            if (!Path.IsPathRooted(fullPath))
            {
                error = "Enter an absolute Windows path.";
                return false;
            }

            return true;
        }
        catch
        {
            error = "Enter a valid Windows path.";
            return false;
        }
    }

    private static bool LooksLikeOracleMiddleware(string middlewareHome)
    {
        if (SafeDiscoveryIO.DirectoryExists(Path.Combine(middlewareHome, "wlserver"))) return true;
        if (SafeDiscoveryIO.DirectoryExists(Path.Combine(middlewareHome, "oracle_common"))) return true;
        if (SafeDiscoveryIO.FileExists(Path.Combine(middlewareHome, "registry.xml"))) return true;
        if (SafeDiscoveryIO.DirectoryExists(Path.Combine(middlewareHome, "inventory"))) return true;
        return false;
    }

    private static bool LooksLikeWebLogicDomain(string domainHome, out string error)
    {
        error = string.Empty;
        var configXml = Path.Combine(domainHome, "config", "config.xml");
        if (!SafeDiscoveryIO.FileExists(configXml))
        {
            error = "config.xml missing under domain config folder.";
            return false;
        }

        if (!SafeDiscoveryIO.DirectoryExists(Path.Combine(domainHome, "bin")))
        {
            error = "Invalid WebLogic domain structure (bin folder not found).";
            return false;
        }

        return true;
    }
}
