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
        sb.Append("exit $(if ($null -eq $p) { 1 } else { $p.ExitCode })");
        return sb.ToString();
    }
}
