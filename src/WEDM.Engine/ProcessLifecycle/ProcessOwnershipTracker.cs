using WEDM.Domain.Models;

namespace WEDM.Engine.ProcessLifecycle;

/// <summary>
/// In-memory store for process ownership records.
///
/// Every process launched by WEDM is registered here immediately after spawn.
/// The tracker distinguishes WEDM-owned processes from external Oracle processes,
/// enabling safe targeted cleanup without killing customer-owned Oracle environments.
///
/// Thread-safety: all public methods are protected by a reader-writer lock.
/// The tracker is designed to be registered as a singleton and used from multiple threads.
/// </summary>
public sealed class ProcessOwnershipTracker
{
    // PID → ownership record for all known WEDM-launched processes
    private readonly Dictionary<int, ProcessOwnershipRecord> _byPid =
        new();

    // SessionId → list of root PIDs owned by that session
    private readonly Dictionary<Guid, List<int>> _bySession =
        new();

    private readonly ReaderWriterLockSlim _lock = new();

    // ─────────────────────────────────────────────────────────────────────────
    // Registration
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records that WEDM has launched a process.  Call immediately after process start
    /// before any await so the OS cannot reuse the PID.
    /// </summary>
    public ProcessOwnershipRecord Register(ProcessLaunchContext context, int pid)
    {
        var record = new ProcessOwnershipRecord
        {
            SessionId       = context.SessionId,
            DeploymentId    = context.DeploymentId,
            AttemptNumber   = context.AttemptNumber,
            RootProcessId   = pid,
            Tool            = context.Tool,
            OracleHome      = context.OracleHome,
            TempDirectory   = context.TempRoot,
            WorkingDirectory = context.WorkingDirectory,
        };

        _lock.EnterWriteLock();
        try
        {
            _byPid[pid] = record;

            if (!_bySession.TryGetValue(context.SessionId, out var list))
            {
                list = new List<int>();
                _bySession[context.SessionId] = list;
            }

            if (!list.Contains(pid))
                list.Add(pid);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return record;
    }

    /// <summary>
    /// Records a child process spawned by a known WEDM-owned parent.
    /// The child inherits the session/deployment identity of the parent.
    /// </summary>
    public void TrackChild(int parentPid, int childPid)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_byPid.TryGetValue(parentPid, out var parent))
                return; // unknown parent — ignore

            // Add child to parent's child list (mutation on record)
            if (!parent.ChildProcessIds.Contains(childPid))
                parent.ChildProcessIds.Add(childPid);

            // Register child as a known PID for ownership queries
            if (!_byPid.ContainsKey(childPid))
            {
                var childRecord = new ProcessOwnershipRecord
                {
                    SessionId        = parent.SessionId,
                    DeploymentId     = parent.DeploymentId,
                    AttemptNumber    = parent.AttemptNumber,
                    RootProcessId    = childPid,
                    Tool             = parent.Tool,
                    OracleHome       = parent.OracleHome,
                    TempDirectory    = parent.TempDirectory,
                    WorkingDirectory = parent.WorkingDirectory,
                };
                _byPid[childPid] = childRecord;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Query
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines the ownership of a given PID.
    /// Returns null when the PID is not registered with any WEDM session.
    /// </summary>
    public ProcessOwnershipRecord? GetOwnership(int pid)
    {
        _lock.EnterReadLock();
        try
        {
            return _byPid.GetValueOrDefault(pid);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns true when the given PID is registered as owned by a WEDM session.
    /// </summary>
    public bool IsWedmOwned(int pid)
        => GetOwnership(pid) is not null;

    /// <summary>
    /// Returns all ownership records for processes registered in the given WEDM session.
    /// Returns an empty list when the session has no registered processes.
    /// </summary>
    public IReadOnlyList<ProcessOwnershipRecord> GetSessionRecords(Guid sessionId)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_bySession.TryGetValue(sessionId, out var pids))
                return [];

            return pids
                .Select(pid => _byPid.GetValueOrDefault(pid))
                .Where(r => r is not null)
                .Cast<ProcessOwnershipRecord>()
                .ToList()
                .AsReadOnly();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns all PIDs owned by the given session (root processes only; children are tracked separately).
    /// </summary>
    public IReadOnlyList<int> GetSessionPids(Guid sessionId)
    {
        _lock.EnterReadLock();
        try
        {
            return _bySession.TryGetValue(sessionId, out var pids)
                ? pids.ToList().AsReadOnly()
                : [];
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns all ownership records owned by the given session AND attempt number.
    /// Used to find processes from a specific retry attempt for targeted cleanup.
    /// </summary>
    public IReadOnlyList<ProcessOwnershipRecord> GetAttemptRecords(Guid sessionId, int attemptNumber)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_bySession.TryGetValue(sessionId, out var pids))
                return [];

            return pids
                .Select(pid => _byPid.GetValueOrDefault(pid))
                .Where(r => r is not null && r.AttemptNumber == attemptNumber)
                .Cast<ProcessOwnershipRecord>()
                .ToList()
                .AsReadOnly();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ownership classification
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies the ownership of a detected process.
    ///
    /// Logic:
    ///   1. If the PID is in the current session's ownership records → WedmOwned.
    ///   2. If the PID is in a prior session's ownership records → WedmPriorSession.
    ///   3. Otherwise → Unknown (may be external).
    /// </summary>
    public (ProcessOwnership ownership, Guid? ownerSession) ClassifyOwnership(
        int pid,
        Guid currentSessionId)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_byPid.TryGetValue(pid, out var record))
                return (ProcessOwnership.Unknown, null);

            if (record.SessionId == currentSessionId)
                return (ProcessOwnership.WedmOwned, currentSessionId);

            return (ProcessOwnership.WedmPriorSession, record.SessionId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Termination recording
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Records that a process has been terminated, updating the ownership record.</summary>
    public void RecordTermination(int pid, TerminationStage stage)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_byPid.TryGetValue(pid, out var record))
            {
                record.TerminatedAt     = DateTimeOffset.UtcNow;
                record.TerminationStage = stage;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Crash recovery / serialization support
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all currently tracked ownership records.
    /// Used by crash recovery to serialize session state.
    /// </summary>
    public IReadOnlyList<ProcessOwnershipRecord> GetAllRecords()
    {
        _lock.EnterReadLock();
        try
        {
            return _byPid.Values.ToList().AsReadOnly();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Loads ownership records from a prior session (e.g. loaded from persisted state during crash recovery).
    /// Records are stored as prior-session records; they do not become WedmOwned for the current session.
    /// </summary>
    public void LoadPriorSessionRecords(IEnumerable<ProcessOwnershipRecord> records)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var record in records)
            {
                var pid = record.RootProcessId;
                if (!_byPid.ContainsKey(pid))
                    _byPid[pid] = record;

                if (!_bySession.TryGetValue(record.SessionId, out var list))
                {
                    list = new List<int>();
                    _bySession[record.SessionId] = list;
                }

                if (!list.Contains(pid))
                    list.Add(pid);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
