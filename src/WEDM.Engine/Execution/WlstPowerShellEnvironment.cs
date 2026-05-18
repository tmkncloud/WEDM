using System.Text;
using WEDM.Domain.Models;

namespace WEDM.Engine.Execution;

/// <summary>Unified ORACLE_HOME / JAVA_HOME / PATH injection for all WLST PowerShell launches.</summary>
public static class WlstPowerShellEnvironment
{
    public static WlstExecutionEnvironment FromDeployment(DeploymentConfiguration config)
        => new()
        {
            OracleHome = config.Paths.MiddlewareHome,
            JavaHome   = ResolveJavaHome(config),
        };

    public static string? ResolveJavaHome(DeploymentConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.Java.JavaHome))
            return config.Java.JavaHome;
        return Environment.GetEnvironmentVariable("JAVA_HOME");
    }

    public static string BuildEnvironmentPowerShell(WlstExecutionEnvironment? environment)
    {
        if (environment is null) return string.Empty;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(environment.OracleHome))
        {
            var oh = environment.OracleHome.Replace("'", "''", StringComparison.Ordinal);
            sb.AppendLine($"$env:ORACLE_HOME = '{oh}'");
        }
        if (!string.IsNullOrWhiteSpace(environment.JavaHome))
        {
            var jh = environment.JavaHome.Replace("'", "''", StringComparison.Ordinal);
            sb.AppendLine($"$env:JAVA_HOME = '{jh}'");
            sb.AppendLine("$env:PATH = \"$env:JAVA_HOME\\bin;$env:PATH\"");
        }
        return sb.ToString().TrimEnd();
    }

    public static string BuildEnvironmentTrace(WlstExecutionEnvironment? environment)
    {
        if (environment is null) return "[WEDM] WLST environment: (process defaults)";
        var oh = string.IsNullOrWhiteSpace(environment.OracleHome) ? "(unset)" : environment.OracleHome;
        var jh = string.IsNullOrWhiteSpace(environment.JavaHome) ? "(unset)" : environment.JavaHome;
        return $"[WEDM] WLST environment: ORACLE_HOME={oh}; JAVA_HOME={jh}";
    }

    /// <summary>
    /// Build a key/value dictionary of environment variables to inject into a child process.
    /// Use this with <see cref="IExternalProcessRunner"/> instead of the PowerShell wrapper.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironmentVariables(
        WlstExecutionEnvironment? environment)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (environment is null) return vars;
        if (!string.IsNullOrWhiteSpace(environment.OracleHome))
            vars["ORACLE_HOME"] = environment.OracleHome;
        if (!string.IsNullOrWhiteSpace(environment.JavaHome))
        {
            vars["JAVA_HOME"] = environment.JavaHome;
            // Prepend to PATH so the correct JVM is found first.
            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            vars["PATH"] = $"{environment.JavaHome}\\bin;{existingPath}";
        }
        return vars;
    }

    /// <summary>PowerShell body: env vars, optional preamble, Start-Process WLST, exit with child code.</summary>
    public static string BuildWlstLaunchBody(
        string wlstCmd,
        string scriptPath,
        WlstExecutionEnvironment? environment,
        string? preamble = null)
    {
        var envLines = BuildEnvironmentPowerShell(environment);
        var envTrace = BuildEnvironmentTrace(environment);
        var wlstQ = "'" + wlstCmd.Replace("'", "''", StringComparison.Ordinal) + "'";
        var pyQ   = "'" + scriptPath.Replace("'", "''", StringComparison.Ordinal) + "'";

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        if (!string.IsNullOrWhiteSpace(envLines))
            sb.AppendLine(envLines);
        sb.AppendLine($"Write-Output '{envTrace.Replace("'", "''", StringComparison.Ordinal)}'");
        if (!string.IsNullOrWhiteSpace(preamble))
            sb.Append(preamble.TrimEnd()).AppendLine();
        sb.AppendLine($"$p = Start-Process -FilePath {wlstQ} -ArgumentList @({pyQ}) -Wait -PassThru -NoNewWindow");
        // Emit a structured exit-code marker that PowerShellExecutor can read from the output stream,
        // preventing the in-process PS SDK's ps.HadErrors flag from causing false-positive failures
        // (WLST and other native commands write informational messages to stderr which populate Streams.Error
        //  even when the child process exits cleanly).
        sb.AppendLine("$__wedm_rc = if ($null -eq $p) { 1 } else { [int]$p.ExitCode }");
        sb.AppendLine("Write-Output \"__WEDM_EXIT:$__wedm_rc\"");
        sb.Append("exit $__wedm_rc");
        return sb.ToString();
    }
}
