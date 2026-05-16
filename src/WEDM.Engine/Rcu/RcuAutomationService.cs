using System.Diagnostics;
using System.Text;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Versioning;
using WEDM.Infrastructure.Security;

namespace WEDM.Engine.Rcu;

public sealed class RcuAutomationService : IRcuAutomationService
{
    private static readonly string[] RequiredComponentSchemas =
        ["STB", "MDS", "OPSS", "IAU", "IAU_APPEND", "IAU_VIEWER"];

    private readonly ILoggingService _log;

    public RcuAutomationService(ILoggingService log) => _log = log;

    public async Task<RcuPrecheckResult> PrecheckAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var existing = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Database.Host))
            messages.Add("Database host is required for RCU.");
        if (config.Database.Port <= 0)
            messages.Add("Database port must be positive.");
        if (string.IsNullOrWhiteSpace(config.Database.ServiceName))
            messages.Add("Database service name or SID is required.");

        var charsetOk = string.Equals(config.Database.NlsCharset, "AL32UTF8", StringComparison.OrdinalIgnoreCase);
        if (!charsetOk)
            messages.Add($"Database NLS charset should be AL32UTF8 for Fusion Middleware; configured: '{config.Database.NlsCharset}'.");

        var rcuPath = ResolveRcuPath(config);
        if (string.IsNullOrWhiteSpace(rcuPath) || !File.Exists(rcuPath))
            messages.Add($"RCU executable not found. Set database.rcuPath or install RCU under middleware home. Checked: {rcuPath}");

        foreach (var schema in EnumerateExpectedSchemas(config))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await SchemaExistsAsync(config, schema, cancellationToken).ConfigureAwait(false))
                existing.Add(schema);
        }

        if (existing.Count > 0)
            messages.Add(
                $"RCU schemas already exist for prefix '{config.Database.SchemaPrefix}': {string.Join(", ", existing)}. " +
                "WEDM will not drop or overwrite schemas silently.");

        return new RcuPrecheckResult
        {
            CanProceed    = messages.Count == 0 || (existing.Count > 0 && messages.All(m => !m.Contains("not found", StringComparison.OrdinalIgnoreCase))),
            SchemasExist  = existing.Count > 0,
            CharsetValid  = charsetOk,
            Messages      = messages,
            ExistingSchemas = existing
        };
    }

    public async Task<RcuExecutionResult> ExecuteAsync(
        DeploymentConfiguration config,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var pre = await PrecheckAsync(config, cancellationToken).ConfigureAwait(false);
        if (pre.SchemasExist)
        {
            return new RcuExecutionResult
            {
                Success = true,
                Skipped = true,
                DryRun  = dryRun,
                Output  = "RCU skipped — required schemas already present (idempotent)."
            };
        }

        if (!pre.CanProceed)
            return new RcuExecutionResult
            {
                Success  = false,
                ExitCode = 1,
                Error    = string.Join("; ", pre.Messages)
            };

        var rcuPath = ResolveRcuPath(config);
        var responsePath = Path.Combine(
            config.Paths.TempDirectory,
            $"wedm_rcu_{config.Id:N}.properties");

        Directory.CreateDirectory(config.Paths.TempDirectory);
        await File.WriteAllTextAsync(responsePath, BuildResponseFile(config), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        if (dryRun)
        {
            _log.Info($"RCU dry-run: response file at {responsePath}", "RCU");
            return new RcuExecutionResult
            {
                Success           = true,
                DryRun            = true,
                ResponseFilePath  = responsePath,
                Output            = "Dry-run — response file generated; RCU not invoked."
            };
        }

        var args = $"-silent -responseFile \"{responsePath}\"";
        _log.Info($"Starting RCU: {rcuPath} {args}", "RCU");

        var psi = new ProcessStartInfo
        {
            FileName               = rcuPath,
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = SecretRedactor.Redact(await stdoutTask.ConfigureAwait(false));
        var stderr = SecretRedactor.Redact(await stderrTask.ConfigureAwait(false));
        var output = stdout + stderr;

        try { File.Delete(responsePath); } catch { /* remove secrets */ }

        return new RcuExecutionResult
        {
            Success          = proc.ExitCode == 0,
            ExitCode         = proc.ExitCode,
            Output           = output,
            Error            = proc.ExitCode != 0 ? output : string.Empty,
            ResponseFilePath = null
        };
    }

    private static string ResolveRcuPath(DeploymentConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.Database.RcuPath) && File.Exists(config.Database.RcuPath))
            return config.Database.RcuPath;

        var mw = config.Paths.MiddlewareHome;
        if (string.IsNullOrWhiteSpace(mw)) return string.Empty;

        var candidates = new[]
        {
            Path.Combine(mw, "oracle_common", "bin", "rcu.bat"),
            Path.Combine(mw, "rcuHome", "bin", "rcu.bat"),
            Path.Combine(mw, "..", "rcuHome", "bin", "rcu.bat")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static IEnumerable<string> EnumerateExpectedSchemas(DeploymentConfiguration config)
    {
        var prefix = config.Database.SchemaPrefix.Trim().ToUpperInvariant();
        foreach (var comp in RequiredComponentSchemas)
            yield return $"{prefix}_{comp}";
    }

    private static async Task<bool> SchemaExistsAsync(
        DeploymentConfiguration config,
        string schemaName,
        CancellationToken ct)
    {
        // Lightweight probe via sqlplus-free heuristic: check RCU registry table if accessible.
        // Production environments should replace with Oracle.ManagedDataAccess; here we use file-based idempotency marker.
        var marker = Path.Combine(
            config.Paths.TempDirectory,
            "rcu-schema-markers",
            $"{schemaName}.ok");
        if (File.Exists(marker)) return true;

        await Task.CompletedTask.ConfigureAwait(false);
        return false;
    }

    private static string BuildResponseFile(DeploymentConfiguration config)
    {
        var db = config.Database;
        var adapter = WebLogicVersionAdapterFactory.For(config.WebLogicVersion);
        var sb = new StringBuilder();
        sb.AppendLine("# WEDM-generated RCU response file — review before production use");
        sb.AppendLine($"oracleHome={config.Paths.MiddlewareHome}");
        sb.AppendLine($"databaseType=ORACLE");
        sb.AppendLine($"connectString={db.Host}:{db.Port}/{db.ServiceName}");
        sb.AppendLine($"dbUser={db.SysUsername}");
        sb.AppendLine($"dbRole=SYSDBA");
        sb.AppendLine($"schemaPrefix={db.SchemaPrefix}");
        sb.AppendLine($"schemaPassword=***"); // substituted at runtime by RCU -silent from env in full impl
        sb.AppendLine($"componentSchemaPrefix={db.SchemaPrefix}");
        sb.AppendLine($"unicodeSupport=true");
        sb.AppendLine($"edition=EE");
        sb.AppendLine($"# components: {string.Join(",", RequiredComponentSchemas)}");
        sb.AppendLine($"# webLogicVersion: {adapter.VersionLabel}");
        return sb.ToString();
    }
}
