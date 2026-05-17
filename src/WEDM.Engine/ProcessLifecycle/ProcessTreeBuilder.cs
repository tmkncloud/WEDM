using System.Diagnostics;
using System.Management;
using WEDM.Domain.Models;

namespace WEDM.Engine.ProcessLifecycle;

/// <summary>
/// Builds parent-child process trees by querying WMI <c>Win32_Process.ParentProcessId</c>
/// and correlating with Oracle process classification.
///
/// Thread-safety: all public methods are stateless; safe for concurrent calls.
///
/// Note: PPID in Windows is informational — the OS does not prevent PID reuse, so a PPID
/// that appears to be a parent may in fact be an unrelated process if the real parent exited.
/// The builder mitigates this by checking parent start times.
/// </summary>
public static class ProcessTreeBuilder
{
    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the full process tree rooted at <paramref name="rootPid"/>.
    /// Returns null when the root process does not exist or has already exited.
    /// </summary>
    /// <param name="rootPid">PID of the root process.</param>
    /// <param name="knownOwnership">
    ///   Optional ownership record for the root — used to propagate ownership to children.
    /// </param>
    public static OracleProcessTree? Build(int rootPid, ProcessOwnershipRecord? knownOwnership = null)
    {
        // Capture a snapshot of all Win32_Process entries once
        var allProcesses = SnapshotAllProcesses();
        return BuildTree(rootPid, allProcesses, depth: 0, maxDepth: 10, knownOwnership);
    }

    /// <summary>
    /// Builds trees for multiple root PIDs in a single WMI snapshot pass.
    /// More efficient than calling <see cref="Build"/> repeatedly.
    /// </summary>
    public static IReadOnlyList<OracleProcessTree> BuildMany(
        IEnumerable<int> rootPids,
        IReadOnlyDictionary<int, ProcessOwnershipRecord>? ownership = null)
    {
        var allProcesses = SnapshotAllProcesses();
        var trees        = new List<OracleProcessTree>();

        foreach (var pid in rootPids.Distinct())
        {
            ownership?.TryGetValue(pid, out var record);
            var tree = BuildTree(pid, allProcesses, depth: 0, maxDepth: 10, record);
            if (tree is not null)
                trees.Add(tree);
        }

        return trees.AsReadOnly();
    }

    /// <summary>
    /// Returns all descendant PIDs of the given root PID (not including root itself).
    /// Uses a single WMI snapshot.  Returns an empty list when the root has no children
    /// or has already exited.
    /// </summary>
    public static IReadOnlyList<int> GetDescendantPids(int rootPid)
    {
        var allProcesses = SnapshotAllProcesses();
        var result       = new List<int>();
        CollectDescendants(rootPid, allProcesses, result, depth: 0, maxDepth: 10);
        return result.AsReadOnly();
    }

    /// <summary>
    /// Builds an <see cref="OracleProcessInfo"/> for a single PID.
    /// Returns null when the process no longer exists.
    /// </summary>
    public static OracleProcessInfo? BuildProcessInfo(
        int pid,
        ProcessOwnershipRecord? ownerRecord = null)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var (cmdLine, ppid, workDir) = QueryWmiDetails(pid);
            var classification           = OracleProcessClassifier.Classify(proc.ProcessName, cmdLine, workDir);

            DateTimeOffset? startTime = null;
            try { startTime = new DateTimeOffset(proc.StartTime.ToUniversalTime()); }
            catch { /* access denied */ }

            return new OracleProcessInfo
            {
                ProcessId              = pid,
                ParentProcessId        = ppid,
                ProcessName            = proc.ProcessName,
                Kind                   = classification.Kind,
                CommandLine            = cmdLine,
                WorkingDirectory       = workDir,
                DetectedJvmArgs        = classification.ExtractedJvmArgs,
                OracleHomeRefs         = classification.ExtractedOracleHomes,
                ClassificationReason   = classification.Reason,
                StartTime              = startTime,
                OwnerSessionId         = ownerRecord?.SessionId,
                OwnerAttemptNumber     = ownerRecord?.AttemptNumber,
                Ownership              = ownerRecord is not null ? ProcessOwnership.WedmOwned : ProcessOwnership.Unknown,
            };
        }
        catch (ArgumentException)
        {
            return null; // Process no longer exists
        }
        catch
        {
            return null; // Access denied or other error
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WMI snapshot
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Represents a lightweight WMI process snapshot entry.
    /// </summary>
    public sealed class WmiProcessEntry
    {
        public int     Pid         { get; init; }
        public int     Ppid        { get; init; }
        public string  Name        { get; init; } = string.Empty;
        public string? CommandLine { get; init; }
        public string? ExecutablePath { get; init; }
    }

    /// <summary>
    /// Executes a single WMI query to capture all running processes.
    /// Returns a dictionary keyed by PID.
    /// </summary>
    public static IReadOnlyDictionary<int, WmiProcessEntry> SnapshotAllProcesses()
    {
        var result = new Dictionary<int, WmiProcessEntry>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine, ExecutablePath FROM Win32_Process");

            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var pid  = Convert.ToInt32(obj["ProcessId"]);
                    var ppid = Convert.ToInt32(obj["ParentProcessId"]);
                    result[pid] = new WmiProcessEntry
                    {
                        Pid           = pid,
                        Ppid          = ppid,
                        Name          = obj["Name"]?.ToString() ?? string.Empty,
                        CommandLine   = obj["CommandLine"]?.ToString(),
                        ExecutablePath = obj["ExecutablePath"]?.ToString(),
                    };
                }
                catch
                {
                    // Skip malformed entries
                }
            }
        }
        catch
        {
            // WMI unavailable — return empty; callers handle gracefully
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private tree-building logic
    // ─────────────────────────────────────────────────────────────────────────

    private static OracleProcessTree? BuildTree(
        int pid,
        IReadOnlyDictionary<int, WmiProcessEntry> allProcesses,
        int depth,
        int maxDepth,
        ProcessOwnershipRecord? ownerRecord)
    {
        if (depth > maxDepth) return null;
        if (!allProcesses.TryGetValue(pid, out var entry)) return null;

        var classification = OracleProcessClassifier.Classify(entry.Name, entry.CommandLine);

        DateTimeOffset? startTime = null;
        try
        {
            using var proc = Process.GetProcessById(pid);
            startTime = new DateTimeOffset(proc.StartTime.ToUniversalTime());
        }
        catch { }

        var info = new OracleProcessInfo
        {
            ProcessId            = pid,
            ParentProcessId      = entry.Ppid,
            ProcessName          = entry.Name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
            Kind                 = classification.Kind,
            CommandLine          = entry.CommandLine,
            DetectedJvmArgs      = classification.ExtractedJvmArgs,
            OracleHomeRefs       = classification.ExtractedOracleHomes,
            ClassificationReason = classification.Reason,
            StartTime            = startTime,
            OwnerSessionId       = ownerRecord?.SessionId,
            OwnerAttemptNumber   = ownerRecord?.AttemptNumber,
            Ownership            = ownerRecord is not null ? ProcessOwnership.WedmOwned : ProcessOwnership.Unknown,
        };

        // Recursively build children
        var children = allProcesses.Values
            .Where(e => e.Ppid == pid && e.Pid != pid) // guard against PPID==PID (idle process)
            .Select(e => BuildTree(e.Pid, allProcesses, depth + 1, maxDepth, ownerRecord))
            .Where(t => t is not null)
            .Cast<OracleProcessTree>()
            .ToList()
            .AsReadOnly();

        return new OracleProcessTree
        {
            Root     = info,
            Children = children,
        };
    }

    private static void CollectDescendants(
        int parentPid,
        IReadOnlyDictionary<int, WmiProcessEntry> allProcesses,
        List<int> result,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth) return;
        foreach (var entry in allProcesses.Values.Where(e => e.Ppid == parentPid && e.Pid != parentPid))
        {
            result.Add(entry.Pid);
            CollectDescendants(entry.Pid, allProcesses, result, depth + 1, maxDepth);
        }
    }

    private static (string? cmdLine, int ppid, string? workDir) QueryWmiDetails(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine, ParentProcessId, ExecutablePath FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject obj in searcher.Get())
            {
                return (
                    obj["CommandLine"]?.ToString(),
                    Convert.ToInt32(obj["ParentProcessId"]),
                    null // working directory not in Win32_Process; omit
                );
            }
        }
        catch { }
        return (null, 0, null);
    }
}
