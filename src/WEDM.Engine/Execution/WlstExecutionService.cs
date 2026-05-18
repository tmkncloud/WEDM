using System.Diagnostics;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Transformation;

namespace WEDM.Engine.Execution;

/// <summary>
/// Executes WLST offline/online scripts for the migration workflow.
///
/// Implementation: uses <see cref="IExternalProcessRunner"/> to launch
///   cmd.exe /c wlst.cmd script.py
/// directly rather than wrapping in a PowerShell Start-Process call.
///
/// That earlier approach deadlocked in WPF hosts because Start-Process -NoNewWindow
/// caused the WLST/java child to inherit the parent's NULL stdio handles, preventing
/// it from writing output and blocking on a full pipe buffer.
/// </summary>
public sealed class WlstExecutionService : IWlstExecutionService
{
    private readonly IExternalProcessRunner _runner;

    public WlstExecutionService(IExternalProcessRunner runner) => _runner = runner;

    public async Task<WlstExecutionRecord> ExecuteScriptAsync(
        string                      wlstCmd,
        string                      scriptPath,
        MigrationExecutionCredentials? credentials,
        bool                        dryRun,
        string                      logDirectory,
        CancellationToken           cancellationToken = default,
        TimeSpan?                   timeout           = null,
        WlstExecutionEnvironment?   environment       = null)
    {
        Directory.CreateDirectory(logDirectory);
        var record = new WlstExecutionRecord
        {
            ScriptName = Path.GetFileName(scriptPath),
            ScriptPath = scriptPath,
            DryRun     = dryRun,
        };

        var sw         = Stopwatch.StartNew();
        string? staged = null;

        try
        {
            // ── 1. Validate inputs ───────────────────────────────────────────
            if (!File.Exists(scriptPath))
            {
                record.Success       = false;
                record.ExitCode      = -1;
                record.StderrExcerpt = $"Script not found: {scriptPath}. "
                                     + "Remediation: verify migration workspace WLST scripts were generated.";
                return record;
            }

            // ── 2. Stage script (credential substitution) ───────────────────
            staged = Path.Combine(logDirectory, $"staged-{record.ScriptName}");
            var content = await File.ReadAllTextAsync(scriptPath, cancellationToken)
                                    .ConfigureAwait(false);

            if (credentials is not null && !string.IsNullOrEmpty(credentials.WebLogicPassword))
            {
                content = content.Replace(
                    "***CHANGE_PASSWORD***",
                    credentials.WebLogicPassword,
                    StringComparison.Ordinal);
                content = content.Replace(
                    "'weblogic'",
                    $"'{credentials.WebLogicUsername.Replace("'", "''", StringComparison.Ordinal)}'",
                    StringComparison.Ordinal);
            }

            await File.WriteAllTextAsync(staged, content, cancellationToken)
                      .ConfigureAwait(false);

            var envTrace = WlstPowerShellEnvironment.BuildEnvironmentTrace(environment);

            // ── 3. Dry-run short-circuit ─────────────────────────────────────
            if (dryRun)
            {
                record.Success       = true;
                record.ExitCode      = 0;
                var wlstNote         = File.Exists(wlstCmd) ? wlstCmd : $"WLST NOT FOUND: {wlstCmd}";
                record.StdoutExcerpt = $"[DRY-RUN] Would execute: {wlstNote} {staged}\n{envTrace}";
                return record;
            }

            if (!File.Exists(wlstCmd))
            {
                record.Success       = false;
                record.ExitCode      = -1;
                record.StderrExcerpt = $"WLST not found: {wlstCmd}. "
                                     + "Remediation: verify middleware home and WLST path resolution.";
                return record;
            }

            // ── 4. Build ExternalProcessOptions ─────────────────────────────
            var envVars = WlstPowerShellEnvironment.BuildEnvironmentVariables(environment);

            var options = new ExternalProcessOptions
            {
                // cmd.exe /c ""wlst.cmd" "script.py""
                FileName                  = "cmd.exe",
                Arguments                 = BuildCmdArguments(wlstCmd, staged),
                WorkingDirectory          = logDirectory,
                EnvironmentVariables      = envVars,
                TotalTimeout              = timeout ?? ExternalProcessTimeouts.DomainCreation,
                SilentProcessTimeout      = ExternalProcessTimeouts.SilentProcess,
                LaunchVerificationTimeout = ExternalProcessTimeouts.LaunchVerification,
                ExpectedChildProcessName  = "java",
                Label                     = $"WLST {record.ScriptName}",
                EnableWatchdog            = true,
            };

            // ── 5. Execute ───────────────────────────────────────────────────
            var result = await _runner.RunAsync(
                options,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            record.ExitCode = result.ExitCode;
            record.Success  = result.Success;

            var rawOut = TransformationSafeIO.MaskSecrets(result.Output);
            var rawErr = TransformationSafeIO.MaskSecrets(result.Errors);
            record.StdoutExcerpt = Truncate(rawOut, 4000);
            record.StderrExcerpt = Truncate(rawErr, 4000);

            if (!record.Success)
            {
                var hint = string.Empty;
                if (result.Hung)
                    hint = " Remediation: check for a blocking UAC prompt or AV interception of java.exe.";
                else if (result.LaunchFailed)
                    hint = $" Remediation: {result.LaunchFailureReason}";
                else if (result.TimedOut)
                    hint = $" Remediation: WLST timed out after {options.TotalTimeout} — check DB connectivity for online scripts.";
                else if (rawErr.Contains("JAVA_HOME", StringComparison.OrdinalIgnoreCase))
                    hint = " Remediation: set Target.JavaHome in migration configuration or install JDK.";

                record.StderrExcerpt = Truncate((record.StderrExcerpt + hint).Trim(), 4000);
            }

            // ── 6. Persist execution log ────────────────────────────────────
            var logPath = Path.Combine(logDirectory, $"{record.ScriptName}.log");
            await File.WriteAllTextAsync(
                logPath,
                $"exit={result.ExitCode}\nPID={result.Pid}\n{envTrace}\n"
              + $"--- stdout ---\n{record.StdoutExcerpt}\n"
              + $"--- stderr ---\n{record.StderrExcerpt}",
                cancellationToken).ConfigureAwait(false);
            record.LogPath = logPath;
        }
        finally
        {
            sw.Stop();
            record.DurationMs = sw.ElapsedMilliseconds;
            if (staged is not null && File.Exists(staged))
            {
                try { File.Delete(staged); }
                catch (IOException ex)
                {
                    record.StderrExcerpt = Truncate(
                        (record.StderrExcerpt ?? "") + $" [cleanup] {ex.Message}", 4000);
                }
            }
        }

        return record;
    }

    /// <summary>
    /// Build CMD /C argument string using the double-quote wrapping rule required
    /// when the compound command contains quoted paths with spaces.
    /// </summary>
    private static string BuildCmdArguments(string wlstCmd, string scriptPath)
        => $"/c \"\"{wlstCmd}\" \"{scriptPath}\"\"";

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}
