using System.IO;

namespace WEDM.UI.Services;

/// <summary>Reusable validation helpers for wizard steps (MVVM-safe, no UI dependencies).</summary>
public static class WizardValidationHelper
{
    public static bool IsRequiredPath(string? path, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "This path is required.";
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            if (!Path.IsPathRooted(full))
            {
                error = "Enter an absolute path (e.g. C:\\Oracle).";
                return false;
            }
        }
        catch
        {
            error = "Enter a valid Windows path.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool IsRequiredText(string? value, string fieldName, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{fieldName} is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool IsValidPort(int port, out string error)
    {
        if (port is > 0 and < 65536)
        {
            error = string.Empty;
            return true;
        }

        error = "Port must be between 1 and 65535.";
        return false;
    }

    public static bool IsValidIdentifier(string? value, string fieldName, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{fieldName} is required.";
            return false;
        }

        if (value.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
        {
            error = $"{fieldName} contains invalid characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
