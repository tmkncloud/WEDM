using WEDM.Domain.Models;

namespace WEDM.Engine.Jdk;

/// <summary>Normalizes Windows JDK installer exit codes (Oracle, MSI, InstallShield).</summary>
public static class JdkExitCodeNormalizer
{
    public static JdkInstallNormalizedResult Normalize(int rawExitCode)
    {
        return rawExitCode switch
        {
            0     => Ok(JdkInstallNormalizedStatus.Success, "Installation completed successfully."),
            3010  => Ok(JdkInstallNormalizedStatus.SuccessRebootRequired, "Installation succeeded — reboot required."),
            1638  => Ok(JdkInstallNormalizedStatus.AlreadyInstalled, "A compatible product is already installed."),
            1641  => Ok(JdkInstallNormalizedStatus.AlreadyInstalled, "Installer reported product already installed."),
            -80   => Fail(JdkInstallNormalizedStatus.InvalidArguments,
                "Invalid installer arguments (exit -80). Verify silent properties and INSTALLDIR path."),
            87    => Fail(JdkInstallNormalizedStatus.InvalidArguments, "Invalid parameter (exit 87)."),
            1603  => Fail(JdkInstallNormalizedStatus.Failed, "Fatal error during installation (exit 1603)."),
            1618  => Fail(JdkInstallNormalizedStatus.Failed, "Another installation is already in progress (exit 1618)."),
            _     => Fail(JdkInstallNormalizedStatus.Failed, $"Installer exited with code {rawExitCode}.")
        };
    }

    private static JdkInstallNormalizedResult Ok(JdkInstallNormalizedStatus status, string message)
        => new() { Status = status, Success = true, Message = message, RawExitCode = 0 };

    private static JdkInstallNormalizedResult Fail(JdkInstallNormalizedStatus status, string message, int code = -1)
        => new() { Status = status, Success = false, Message = message, RawExitCode = code };
}

public sealed class JdkInstallNormalizedResult
{
    public JdkInstallNormalizedStatus Status { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int RawExitCode { get; init; }
    public bool RebootRequired =>
        Status == JdkInstallNormalizedStatus.SuccessRebootRequired;
}
