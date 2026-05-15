using System.Net;
using System.Net.Sockets;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Transformation;
using WEDM.Engine.Transformation.Wlst;

namespace WEDM.Engine.Execution;

public sealed class MigrationPreflightValidator : IMigrationPreflightValidator
{
    public PreflightValidationResult Validate(MigrationConfiguration configuration, MigrationExecutionOptions options)
    {
        var result = new PreflightValidationResult();
        var checks = new List<PreflightCheckResult>();

        if (!configuration.TransformationCompleted)
            checks.Add(Blocker("Transformation", "Migration preparation must complete before execution."));

        var workspace = configuration.TransformationWorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            checks.Add(Blocker("Workspace", "Migration workspace not found."));
        else if (!File.Exists(Path.Combine(workspace, MigrationWorkspaceManager.ManifestFile)))
            checks.Add(Warning("Workspace", "Workspace manifest missing — regeneration recommended."));

        var mw = configuration.Target.MiddlewareHome ?? configuration.Source.MiddlewareHome;
        if (string.IsNullOrWhiteSpace(mw))
            checks.Add(Blocker("MiddlewareHome", "Target middleware home is required."));
        else
        {
            var wlst = ResolveWlst(mw);
            if (!File.Exists(wlst))
                checks.Add(Blocker("WLST", $"WLST not found at '{wlst}'."));
            else
                checks.Add(Info("WLST", $"WLST located: {wlst}"));
        }

        var javaHome = configuration.Target.JavaHome ?? configuration.Source.JavaHome ?? Environment.GetEnvironmentVariable("JAVA_HOME");
        if (string.IsNullOrWhiteSpace(javaHome))
            checks.Add(Warning("Java", "JAVA_HOME not specified — verify JDK is on PATH for WLST."));
        else if (!Directory.Exists(javaHome))
            checks.Add(Blocker("Java", $"JAVA_HOME path not found: {javaHome}"));
        else
            checks.Add(Info("Java", $"JAVA_HOME: {javaHome}"));

        var targetDomain = options.TargetDomainHome
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WEDM", "target-domains", configuration.Topology.DomainName ?? "migration_domain");

        try
        {
            Directory.CreateDirectory(targetDomain);
            var probe = Path.Combine(targetDomain, ".wedm-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            checks.Add(Info("Permissions", $"Target domain path writable: {targetDomain}"));
        }
        catch (Exception ex)
        {
            checks.Add(Blocker("Permissions", $"Cannot write target domain path: {ex.Message}"));
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(targetDomain)) ?? "C:\\");
            if (drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
                checks.Add(Warning("DiskSpace", $"Low disk space on target volume ({drive.AvailableFreeSpace / (1024 * 1024)} MB free)."));
            else
                checks.Add(Info("DiskSpace", "Sufficient disk space on target volume."));
        }
        catch { checks.Add(Warning("DiskSpace", "Could not evaluate disk space.")); }

        var port = configuration.DomainAnalysis.AdminListenPort ?? 7001;
        if (IsPortInUse(port))
            checks.Add(Warning("Ports", $"Admin port {port} appears in use — verify target environment."));

        if (options.Credentials is null || string.IsNullOrWhiteSpace(options.Credentials.WebLogicPassword))
            checks.Add(Warning("Credentials", "WebLogic credentials not supplied — online WLST scripts will require operator injection."));

        result.Checks = checks;
        result.BlockerCount = checks.Count(c => c.Severity == PreflightSeverity.Blocker);
        result.WarningCount = checks.Count(c => c.Severity == PreflightSeverity.Warning);
        result.Passed = result.BlockerCount == 0;
        return result;
    }

    private static string ResolveWlst(string middlewareHome)
    {
        var candidates = new[]
        {
            Path.Combine(middlewareHome, "oracle_common", "common", "bin", "wlst.cmd"),
            Path.Combine(middlewareHome, "wlserver", "common", "bin", "wlst.cmd"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static PreflightCheckResult Blocker(string name, string msg) => new() { Name = name, Severity = PreflightSeverity.Blocker, Message = msg };
    private static PreflightCheckResult Warning(string name, string msg) => new() { Name = name, Severity = PreflightSeverity.Warning, Message = msg };
    private static PreflightCheckResult Info(string name, string msg) => new() { Name = name, Severity = PreflightSeverity.Informational, Message = msg };
}
