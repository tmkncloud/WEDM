using WEDM.Domain.Models;

namespace WEDM.Engine.Payload;

/// <summary>Normalizes Windows installer exit codes for enterprise reporting.</summary>
public static class InstallerExitCodes
{
    public const int Success          = 0;
    public const int RebootRequired   = 3010;
    public const int AlreadyInstalled = 1638;

    public static bool IsSuccess(int exitCode)
        => exitCode is Success or RebootRequired or AlreadyInstalled;

    public static string DescribeOutcome(int exitCode) => exitCode switch
    {
        Success          => "Installed Successfully",
        RebootRequired   => "Reboot Required",
        AlreadyInstalled => "Already Installed",
        _                => $"Failed (exit {exitCode})"
    };

    public static StepExecutionResult ToStepResult(
        int exitCode,
        string component,
        TimeSpan elapsed,
        string? detail = null)
    {
        var msg = DescribeOutcome(exitCode);
        if (!string.IsNullOrWhiteSpace(detail))
            msg += $": {detail}";

        return exitCode switch
        {
            Success          => StepExecutionResult.Ok(msg, elapsed),
            RebootRequired   => StepExecutionResult.Ok(msg, elapsed),
            AlreadyInstalled => StepExecutionResult.Ok(msg, elapsed),
            _                => StepExecutionResult.Fail($"{component} {msg}", exitCode)
        };
    }
}
