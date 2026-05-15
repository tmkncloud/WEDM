using System.Diagnostics;
using System.Text;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Transformation;

namespace WEDM.Engine.Execution;

public sealed class WlstExecutionService : IWlstExecutionService
{
    private readonly IPowerShellExecutor _ps;

    public WlstExecutionService(IPowerShellExecutor ps) => _ps = ps;

    public async Task<WlstExecutionRecord> ExecuteScriptAsync(
        string wlstCmd,
        string scriptPath,
        MigrationExecutionCredentials? credentials,
        bool dryRun,
        string logDirectory,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        WlstExecutionEnvironment? environment = null)
    {
        Directory.CreateDirectory(logDirectory);
        var record = new WlstExecutionRecord
        {
            ScriptName = Path.GetFileName(scriptPath),
            ScriptPath = scriptPath,
            DryRun     = dryRun,
        };

        var sw = Stopwatch.StartNew();
        string? stagedPath = null;

        try
        {
            if (!File.Exists(scriptPath))
            {
                record.Success  = false;
                record.ExitCode = -1;
                record.StderrExcerpt = $"Script not found: {scriptPath}. Remediation: verify migration workspace WLST scripts were generated.";
                return record;
            }

            stagedPath = Path.Combine(logDirectory, $"staged-{record.ScriptName}");
            var content = await File.ReadAllTextAsync(scriptPath, cancellationToken);
            if (credentials is not null && !string.IsNullOrEmpty(credentials.WebLogicPassword))
            {
                content = content.Replace("***CHANGE_PASSWORD***", credentials.WebLogicPassword, StringComparison.Ordinal);
                content = content.Replace("'weblogic'", $"'{credentials.WebLogicUsername.Replace("'", "''", StringComparison.Ordinal)}'", StringComparison.Ordinal);
            }

            await File.WriteAllTextAsync(stagedPath, content, cancellationToken);

            var envLines = BuildEnvironmentPowerShell(environment);
            var envTrace = BuildEnvironmentTrace(environment);

            if (dryRun)
            {
                record.Success = true;
                record.ExitCode = 0;
                var wlstNote = File.Exists(wlstCmd) ? wlstCmd : $"WLST NOT FOUND: {wlstCmd}";
                record.StdoutExcerpt = $"[DRY-RUN] Would execute: {wlstNote} {stagedPath}\n{envTrace}";
                return record;
            }

            if (!File.Exists(wlstCmd))
            {
                record.Success  = false;
                record.ExitCode = -1;
                record.StderrExcerpt = $"WLST not found: {wlstCmd}. Remediation: verify middleware home and WLST path resolution.";
                return record;
            }

            var wlstQ = "'" + wlstCmd.Replace("'", "''", StringComparison.Ordinal) + "'";
            var pyQ   = "'" + stagedPath.Replace("'", "''", StringComparison.Ordinal) + "'";
            var body  = $"""
                $ErrorActionPreference = 'Continue'
                {envLines}
                Write-Output '{envTrace.Replace("'", "''", StringComparison.Ordinal)}'
                $p = Start-Process -FilePath {wlstQ} -ArgumentList @({pyQ}) -Wait -PassThru -NoNewWindow
                """.TrimEnd()
                + Environment.NewLine
                + "exit $(if ($null -eq $p) { 1 } else { $p.ExitCode })";

            var result = await _ps.ExecuteCommandAsync(
                body.Trim(),
                workingDirectory: logDirectory,
                cancellationToken: cancellationToken,
                operationTimeout: timeout ?? TimeSpan.FromMinutes(90));

            record.ExitCode      = result.ExitCode;
            record.Success       = result.Success && result.ExitCode == 0;
            record.StdoutExcerpt = Truncate(TransformationSafeIO.MaskSecrets(result.Output), 4000);
            record.StderrExcerpt = Truncate(TransformationSafeIO.MaskSecrets(result.Errors), 4000);

            if (!record.Success)
            {
                var hint = record.StderrExcerpt.Contains("JAVA_HOME", StringComparison.OrdinalIgnoreCase)
                    ? " Remediation: set Target.JavaHome in migration configuration or install JDK."
                    : string.Empty;
                record.StderrExcerpt = (record.StderrExcerpt + hint).Trim();
            }

            var logPath = Path.Combine(logDirectory, $"{record.ScriptName}.log");
            await File.WriteAllTextAsync(logPath,
                $"exit={result.ExitCode}\n{envTrace}\n--- stdout ---\n{record.StdoutExcerpt}\n--- stderr ---\n{record.StderrExcerpt}",
                cancellationToken);
            record.LogPath = logPath;
        }
        finally
        {
            sw.Stop();
            record.DurationMs = sw.ElapsedMilliseconds;
            if (stagedPath is not null && File.Exists(stagedPath))
            {
                try { File.Delete(stagedPath); }
                catch (IOException ex)
                {
                    record.StderrExcerpt = Truncate((record.StderrExcerpt ?? "") + $" [cleanup] {ex.Message}", 4000);
                }
            }
        }

        return record;
    }

    internal static string BuildEnvironmentPowerShell(WlstExecutionEnvironment? environment)
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
            sb.AppendLine($"$env:PATH = \"$env:JAVA_HOME\\bin;$env:PATH\"");
        }
        return sb.ToString().TrimEnd();
    }

    internal static string BuildEnvironmentTrace(WlstExecutionEnvironment? environment)
    {
        if (environment is null) return "[WEDM] WLST environment: (process defaults)";
        var oh = string.IsNullOrWhiteSpace(environment.OracleHome) ? "(unset)" : environment.OracleHome;
        var jh = string.IsNullOrWhiteSpace(environment.JavaHome) ? "(unset)" : environment.JavaHome;
        return $"[WEDM] WLST environment: ORACLE_HOME={oh}; JAVA_HOME={jh}";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}
