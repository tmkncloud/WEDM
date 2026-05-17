using System.Xml.Linq;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Discovery.Parsers;

namespace WEDM.Engine.OracleInventory;

/// <summary>
/// Production implementation of <see cref="IOracleInventoryService"/>.
///
/// Handles the Oracle Central Inventory XML (ContentsXML/inventory.xml) lifecycle:
///   - Parsing and snapshot capture
///   - Home registration detection
///   - Lock file detection
///   - Pre-install and post-install validation
///   - Safe XML mutation with backup
///
/// Oracle inventory.xml structure (simplified):
/// <code>
///   &lt;INVENTORY&gt;
///     &lt;HOME_LIST&gt;
///       &lt;HOME NAME="OraHome1" LOC="C:\Oracle\MW" TYPE="O" IDX="1"/&gt;
///     &lt;/HOME_LIST&gt;
///     &lt;COMPOSITEHOME_LIST&gt;
///       &lt;HOME NAME="OraHome1" LOC="C:\Oracle\MW" TYPE="O" IDX="1"&gt;...&lt;/HOME&gt;
///     &lt;/COMPOSITEHOME_LIST&gt;
///   &lt;/INVENTORY&gt;
/// </code>
/// </summary>
public sealed class OracleInventoryService : IOracleInventoryService
{
    private readonly ILoggingService _log;

    // Lock file patterns inside the inventory directory
    private static readonly string[] LockFilePatterns =
    [
        "*.lock",
        ".orainventory.lock",
        "orainventory.lock",
        "inventory.lock",
    ];

    // Subdirectory names that indicate a complete Oracle FMW home
    private static readonly string[] CompleteHomeMarkers =
    [
        "wlserver",
        "oracle_common",
    ];

    // Any one of these being present suggests OUI touched the folder
    private static readonly string[] PartialHomeIndicators =
    [
        "inventory",
        "oui",
        "cfgtoollogs",
        "jdk",
        "jre",
    ];

    public OracleInventoryService(ILoggingService log)
    {
        _log = log;
    }

    // ── Read operations ───────────────────────────────────────────────────────

    public OracleInventorySnapshot? ReadSnapshot(string oracleInventoryPath)
    {
        if (string.IsNullOrWhiteSpace(oracleInventoryPath))
            return null;

        var xmlPath = ResolveInventoryXmlPath(oracleInventoryPath);
        if (xmlPath is null)
        {
            _log.Info(
                $"Oracle inventory directory '{oracleInventoryPath}' has no inventory.xml — treating as empty.",
                "OracleInventory");
            return new OracleInventorySnapshot
            {
                InventoryLoc     = oracleInventoryPath,
                InventoryState   = OracleCentralInventoryState.Missing,
                InventoryHealthy = false,
                InventoryWarning = $"inventory.xml not found under '{oracleInventoryPath}'.",
            };
        }

        var snapshot = OracleInventoryXmlParser.ParseInventoryXml(xmlPath);
        snapshot.InventoryLoc = oracleInventoryPath;

        _log.Info(
            $"Oracle inventory read: {snapshot.OracleHomes.Count} home(s) registered at '{oracleInventoryPath}'.",
            "OracleInventory");

        foreach (var home in snapshot.OracleHomes)
            _log.Verbose($"  Registered home: LOC='{home.Path}' NAME='{home.Name}'", "OracleInventory");

        return snapshot;
    }

    public string? ResolveInventoryXmlPath(string oracleInventoryPath)
    {
        if (string.IsNullOrWhiteSpace(oracleInventoryPath))
            return null;

        // Standard Oracle layout: <inventory>/ContentsXML/inventory.xml
        var contentsXml = Path.Combine(oracleInventoryPath, "ContentsXML", "inventory.xml");
        if (File.Exists(contentsXml))
            return contentsXml;

        // Fallback: inventory.xml directly under the inventory root
        var rootXml = Path.Combine(oracleInventoryPath, "inventory.xml");
        if (File.Exists(rootXml))
            return rootXml;

        return null;
    }

    // ── Detection ─────────────────────────────────────────────────────────────

    public bool IsHomeRegistered(string middlewareHome, string oracleInventoryPath)
    {
        if (string.IsNullOrWhiteSpace(middlewareHome) || string.IsNullOrWhiteSpace(oracleInventoryPath))
            return false;

        var snapshot = ReadSnapshot(oracleInventoryPath);
        if (snapshot is null)
            return false;

        var target = NormalizePath(middlewareHome);
        return snapshot.OracleHomes.Any(h => NormalizePath(h.Path) == target);
    }

    public OracleHomeState DetectHomeState(string middlewareHome, string oracleInventoryPath)
    {
        if (string.IsNullOrWhiteSpace(middlewareHome))
            return OracleHomeState.Unknown;

        // Check for inventory lock first — takes precedence
        if (!string.IsNullOrWhiteSpace(oracleInventoryPath))
        {
            var locks = DetectLocks(oracleInventoryPath);
            if (locks.Count > 0)
            {
                _log.Warning(
                    $"Oracle inventory lock detected: {locks[0].LockFilePath}",
                    "OracleInventory");
                return OracleHomeState.InventoryLocked;
            }
        }

        var folderExists  = Directory.Exists(middlewareHome);
        var isRegistered  = !string.IsNullOrWhiteSpace(oracleInventoryPath)
                            && IsHomeRegistered(middlewareHome, oracleInventoryPath);

        if (isRegistered && folderExists)
        {
            // Check if the folder looks complete
            if (IsHomeStructureComplete(middlewareHome))
                return OracleHomeState.RegisteredAndPresent;

            // Registered but folder is incomplete — weird but possible after failed deinstall
            return OracleHomeState.PartialInstall;
        }

        if (isRegistered && !folderExists)
            return OracleHomeState.RegisteredOrphaned;

        if (!isRegistered && folderExists)
        {
            if (IsPartialInstall(middlewareHome))
                return OracleHomeState.PartialInstall;

            if (IsHomeStructureComplete(middlewareHome))
                return OracleHomeState.UnregisteredInstall;

            // Folder exists but doesn't look like an Oracle home — probably just a directory
            return OracleHomeState.Clean;
        }

        // Not registered, folder doesn't exist
        return OracleHomeState.Clean;
    }

    public IReadOnlyList<OracleHomeDescriptor> FindOrphanedHomes(string oracleInventoryPath)
    {
        var snapshot = ReadSnapshot(oracleInventoryPath);
        if (snapshot is null)
            return [];

        var orphaned = snapshot.OracleHomes
            .Where(h => !string.IsNullOrWhiteSpace(h.Path) && !Directory.Exists(h.Path))
            .ToList();

        if (orphaned.Count > 0)
        {
            _log.Info(
                $"Oracle inventory: {orphaned.Count} orphaned home(s) detected (registered but folder missing).",
                "OracleInventory");
            foreach (var h in orphaned)
                _log.Warning($"  Orphaned: LOC='{h.Path}' NAME='{h.Name}'", "OracleInventory");
        }

        return orphaned.AsReadOnly();
    }

    public bool IsPartialInstall(string middlewareHome)
    {
        if (!Directory.Exists(middlewareHome))
            return false;

        // If ALL complete markers are present, it's not partial — it's complete
        if (IsHomeStructureComplete(middlewareHome))
            return false;

        // If any partial indicator exists, this was touched by OUI
        foreach (var indicator in PartialHomeIndicators)
        {
            if (Directory.Exists(Path.Combine(middlewareHome, indicator))
                || File.Exists(Path.Combine(middlewareHome, indicator)))
                return true;
        }

        // Check if directory has any content at all — even just a few files is suspicious
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(middlewareHome)
                .Take(3)
                .ToList();
            return entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<OracleInventoryLockDescriptor> DetectLocks(string oracleInventoryPath)
    {
        if (string.IsNullOrWhiteSpace(oracleInventoryPath) || !Directory.Exists(oracleInventoryPath))
            return [];

        var found = new List<OracleInventoryLockDescriptor>();

        // Standard lock subdirectory
        var locksDir = Path.Combine(oracleInventoryPath, "locks");

        // Search locations: inventory root and locks/ subdirectory
        var searchDirs = new[] { oracleInventoryPath, locksDir }
            .Where(Directory.Exists);

        foreach (var dir in searchDirs)
        {
            foreach (var pattern in LockFilePatterns)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                    {
                        var info  = new FileInfo(file);
                        var stale = (DateTimeOffset.UtcNow - info.LastWriteTimeUtc) > TimeSpan.FromHours(4);
                        found.Add(new OracleInventoryLockDescriptor
                        {
                            LockFilePath = file,
                            LastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                            IsStale      = stale,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _log.Verbose($"Lock scan skipped for pattern '{pattern}' in '{dir}': {ex.Message}", "OracleInventory");
                }
            }
        }

        return found.AsReadOnly();
    }

    // ── Pre-install validation ────────────────────────────────────────────────

    public OracleInventoryValidationResult ValidateForInstall(
        string middlewareHome,
        string oracleInventoryPath)
    {
        _log.Info(
            $"Oracle inventory pre-install validation: target='{middlewareHome}' inventory='{oracleInventoryPath}'",
            "OracleInventory");

        var findings    = new List<string>();
        var remediation = new List<string>();
        bool canProceed = true;

        // ── 1. Read inventory snapshot ─────────────────────────────────────
        OracleInventorySnapshot? snapshot = null;
        if (!string.IsNullOrWhiteSpace(oracleInventoryPath))
        {
            snapshot = ReadSnapshot(oracleInventoryPath);
            if (snapshot is not null)
            {
                switch (snapshot.InventoryState)
                {
                    case OracleCentralInventoryState.Missing:
                        canProceed = false;
                        findings.Add($"BLOCKED: Central inventory.xml is missing under '{oracleInventoryPath}'.");
                        remediation.Add("Create or restore ContentsXML/inventory.xml before running OUI.");
                        break;
                    case OracleCentralInventoryState.Corrupted:
                        canProceed = false;
                        findings.Add($"BLOCKED: Central inventory.xml is corrupt or unreadable: {snapshot.InventoryWarning}");
                        remediation.Add("Repair or regenerate central inventory.xml before install.");
                        break;
                    case OracleCentralInventoryState.Empty:
                        findings.Add(OracleCentralInventoryClassifier.EmptyInventoryMessage);
                        break;
                }
            }
        }

        // ── 2. Lock file check ────────────────────────────────────────────
        var locks = !string.IsNullOrWhiteSpace(oracleInventoryPath)
            ? DetectLocks(oracleInventoryPath)
            : (IReadOnlyList<OracleInventoryLockDescriptor>)[];

        bool isLocked = false;
        if (locks.Count > 0)
        {
            var activeLocks = locks.Where(l => !l.IsStale).ToList();
            if (activeLocks.Count > 0)
            {
                isLocked    = true;
                canProceed  = false;
                findings.Add($"BLOCKED: Oracle inventory is locked by {activeLocks.Count} active lock file(s).");
                foreach (var lk in activeLocks)
                    findings.Add($"  Lock file: '{lk.LockFilePath}' (modified: {lk.LastModified:u})");
                remediation.Add("Wait for the active OUI or OPatch operation to complete, then retry.");
                remediation.Add("If no Oracle operation is running, manually delete the lock file(s) listed above.");
            }
            else
            {
                findings.Add($"INFO: {locks.Count} stale lock file(s) found (age > 4h) — treating as inactive.");
            }
        }
        else
        {
            findings.Add("Oracle inventory lock check: no active locks detected ✔");
        }

        // ── 3. Existing registration check ────────────────────────────────
        var conflicting = new List<OracleHomeDescriptor>();
        if (snapshot is not null)
        {
            var target   = NormalizePath(middlewareHome);
            var existing = snapshot.OracleHomes
                .Where(h => NormalizePath(h.Path) == target)
                .ToList();

            if (existing.Count > 0)
            {
                canProceed = false;
                findings.Add($"BLOCKED: Oracle Home '{middlewareHome}' is already registered in the Central Inventory.");
                foreach (var h in existing)
                    findings.Add($"  Registered entry: NAME='{h.Name}' LOC='{h.Path}'");
                remediation.Add($"Remove the stale inventory registration for '{middlewareHome}' using WEDM rollback or manually edit ContentsXML/inventory.xml.");
                remediation.Add("After removing the registration, ensure the target directory is also deleted before retrying.");
                conflicting.AddRange(existing);
            }
            else
            {
                findings.Add($"Oracle inventory registration check: '{middlewareHome}' not registered ✔");
            }
        }

        // ── 4. Partial install check ──────────────────────────────────────
        if (IsPartialInstall(middlewareHome))
        {
            canProceed = false;
            findings.Add($"BLOCKED: Target directory '{middlewareHome}' exists and contains partial Oracle Home artifacts.");
            remediation.Add($"Delete the partial directory '{middlewareHome}' completely before retrying the installation.");
            remediation.Add("If this directory contains a previous installation, run WEDM rollback first.");
        }
        else if (Directory.Exists(middlewareHome))
        {
            // Directory exists but is empty — OUI is OK with this in some modes
            findings.Add($"INFO: Target directory '{middlewareHome}' already exists but appears empty — OUI may proceed.");
        }
        else
        {
            findings.Add($"Target directory '{middlewareHome}' does not exist — will be created by OUI ✔");
        }

        // ── 5. Orphaned home scan ─────────────────────────────────────────
        var orphaned = !string.IsNullOrWhiteSpace(oracleInventoryPath)
            ? FindOrphanedHomes(oracleInventoryPath)
            : (IReadOnlyList<OracleHomeDescriptor>)[];
        if (orphaned.Count > 0)
        {
            findings.Add($"INFO: {orphaned.Count} orphaned home registration(s) found (registered but directory missing).");
            findings.Add("These do not block installation but should be cleaned up to prevent future INST-07319 errors.");
        }

        // ── Summary ───────────────────────────────────────────────────────
        var state = DetectHomeState(middlewareHome, oracleInventoryPath);
        _log.Info(
            $"Oracle inventory pre-install: state={state} canProceed={canProceed} findings={findings.Count}",
            "OracleInventory");

        return new OracleInventoryValidationResult
        {
            CanProceed         = canProceed,
            HomeState          = state,
            TargetMiddlewareHome = middlewareHome,
            OracleInventoryPath  = oracleInventoryPath,
            Snapshot             = snapshot,
            Findings             = findings.AsReadOnly(),
            RemediationSteps     = remediation.AsReadOnly(),
            IsLocked             = isLocked,
            OrphanedHomes        = orphaned,
            ConflictingHomes     = conflicting.AsReadOnly(),
        };
    }

    // ── Post-install validation ───────────────────────────────────────────────

    public OracleInventoryValidationResult ValidateAfterInstall(
        string middlewareHome,
        string oracleInventoryPath)
    {
        _log.Info(
            $"Oracle inventory post-install validation: target='{middlewareHome}'",
            "OracleInventory");

        var findings    = new List<string>();
        var remediation = new List<string>();
        bool canProceed = true;

        // ── 1. Re-read inventory after OUI ────────────────────────────────
        var snapshot = !string.IsNullOrWhiteSpace(oracleInventoryPath)
            ? ReadSnapshot(oracleInventoryPath)
            : null;

        // ── 2. Check home is now registered ───────────────────────────────
        if (snapshot is not null)
        {
            var target    = NormalizePath(middlewareHome);
            var registered = snapshot.OracleHomes.Any(h => NormalizePath(h.Path) == target);

            if (registered)
            {
                findings.Add($"Oracle inventory post-install: '{middlewareHome}' is registered ✔");
            }
            else
            {
                // Not fatal — some OUI versions write to a per-home local inventory rather than Central
                findings.Add($"WARNING: '{middlewareHome}' was not found in Central Inventory after OUI exit.");
                findings.Add("This may indicate OUI wrote to a local inventory only — verify manually.");
                remediation.Add("Run 'opatch lsinventory' to check what was registered.");
            }
        }
        else
        {
            findings.Add("INFO: Could not read Oracle Central Inventory after installation (path not configured).");
        }

        // ── 3. Verify directory structure is complete ─────────────────────
        if (!Directory.Exists(middlewareHome))
        {
            canProceed = false;
            findings.Add($"FAIL: Middleware Home directory '{middlewareHome}' does not exist after OUI reported success.");
            remediation.Add("Check OUI log files for the root cause. The installer may have exited 0 but failed silently.");
        }
        else if (IsHomeStructureComplete(middlewareHome))
        {
            findings.Add($"Middleware Home structure is complete ✔ (wlserver/, oracle_common/ present)");
        }
        else
        {
            findings.Add($"WARNING: Middleware Home '{middlewareHome}' exists but key subdirectories are missing.");
            findings.Add("Expected: wlserver/, oracle_common/");
            remediation.Add("The installation may be incomplete. Check OUI logs for errors during file copy phase.");
        }

        var state = DetectHomeState(middlewareHome, oracleInventoryPath);
        _log.Info(
            $"Oracle inventory post-install: state={state} canProceed={canProceed}",
            "OracleInventory");

        return new OracleInventoryValidationResult
        {
            CanProceed          = canProceed,
            HomeState           = state,
            TargetMiddlewareHome = middlewareHome,
            OracleInventoryPath  = oracleInventoryPath,
            Snapshot             = snapshot,
            Findings             = findings.AsReadOnly(),
            RemediationSteps     = remediation.AsReadOnly(),
        };
    }

    // ── Mutation ──────────────────────────────────────────────────────────────

    public string? BackupInventoryXml(string oracleInventoryPath)
    {
        var xmlPath = ResolveInventoryXmlPath(oracleInventoryPath);
        if (xmlPath is null)
        {
            _log.Info("BackupInventoryXml: inventory.xml not found — nothing to back up.", "OracleInventory");
            return null;
        }

        var timestamp  = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var backupPath = xmlPath + $".backup_{timestamp}";
        try
        {
            File.Copy(xmlPath, backupPath, overwrite: false);
            _log.Info($"Oracle inventory backup created: '{backupPath}'", "OracleInventory");
            return backupPath;
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not create inventory backup at '{backupPath}': {ex.Message}", "OracleInventory");
            return null;
        }
    }

    public OracleInventoryRemovalResult RemoveHomeEntry(
        string middlewareHome,
        string oracleInventoryPath)
    {
        _log.Info(
            $"Oracle inventory removal: removing '{middlewareHome}' from '{oracleInventoryPath}'",
            "OracleInventory");

        try
        {
            var xmlPath = ResolveInventoryXmlPath(oracleInventoryPath);
            if (xmlPath is null)
            {
                _log.Info("RemoveHomeEntry: inventory.xml not found — nothing to remove.", "OracleInventory");
                return OracleInventoryRemovalResult.NotFound(
                    Path.Combine(oracleInventoryPath, "ContentsXML", "inventory.xml"));
            }

            // ── Snapshot before ───────────────────────────────────────────
            var snapshotBefore = ReadSnapshot(oracleInventoryPath)
                                 ?? new OracleInventorySnapshot { InventoryLoc = oracleInventoryPath };

            var target = NormalizePath(middlewareHome);
            bool homePresent = snapshotBefore.OracleHomes.Any(h => NormalizePath(h.Path) == target);

            if (!homePresent)
            {
                _log.Info(
                    $"RemoveHomeEntry: '{middlewareHome}' is not registered in Central Inventory — nothing to remove.",
                    "OracleInventory");
                return OracleInventoryRemovalResult.NotFound(xmlPath);
            }

            // ── Backup before mutation ────────────────────────────────────
            var backupPath = BackupInventoryXml(oracleInventoryPath);

            // ── Load and mutate XML ───────────────────────────────────────
            var doc     = XDocument.Load(xmlPath);
            var removed = new List<string>();

            // Remove from HOME_LIST
            RemoveMatchingHomeElements(doc, target, "HOME_LIST", "HOME", removed);

            // Remove from COMPOSITEHOME_LIST
            RemoveMatchingHomeElements(doc, target, "COMPOSITEHOME_LIST", "HOME", removed);

            // Remove loose HOME elements at root (some older Oracle versions)
            var looseHomes = doc.Root?
                .Elements("HOME")
                .Where(e => NormalizePath(e.Attribute("LOC")?.Value ?? string.Empty) == target)
                .ToList() ?? [];
            foreach (var el in looseHomes)
            {
                removed.Add($"ROOT/HOME[@LOC='{el.Attribute("LOC")?.Value}']");
                el.Remove();
            }

            if (removed.Count == 0)
            {
                _log.Warning(
                    "RemoveHomeEntry: home was in snapshot but not found in live XML — may have been removed concurrently.",
                    "OracleInventory");
                return OracleInventoryRemovalResult.NotFound(xmlPath);
            }

            // ── Write updated XML atomically ──────────────────────────────
            var tempPath = xmlPath + ".wedm_tmp";
            using (var writer = new StreamWriter(tempPath, append: false, System.Text.Encoding.UTF8))
                doc.Save(writer);

            File.Move(tempPath, xmlPath, overwrite: true);

            _log.Info(
                $"RemoveHomeEntry: removed {removed.Count} element(s) from '{xmlPath}': {string.Join(", ", removed)}",
                "OracleInventory");

            // ── Snapshot after ────────────────────────────────────────────
            var snapshotAfter = ReadSnapshot(oracleInventoryPath)
                                ?? new OracleInventorySnapshot { InventoryLoc = oracleInventoryPath };

            // ── Verify removal ────────────────────────────────────────────
            var stillPresent = snapshotAfter.OracleHomes.Any(h => NormalizePath(h.Path) == target);
            if (stillPresent)
            {
                _log.Error(
                    $"RemoveHomeEntry: VERIFICATION FAILED — '{middlewareHome}' still appears in inventory after removal.",
                    category: "OracleInventory");
                return OracleInventoryRemovalResult.Failed(
                    $"Verification failed: '{middlewareHome}' still registered after removal attempt. " +
                    $"Backup preserved at: '{backupPath}'.",
                    xmlPath);
            }

            _log.Info(
                $"RemoveHomeEntry: verification passed — '{middlewareHome}' no longer in Central Inventory ✔",
                "OracleInventory");

            return OracleInventoryRemovalResult.Removed(
                xmlPath, backupPath, removed.AsReadOnly(), snapshotBefore, snapshotAfter);
        }
        catch (Exception ex)
        {
            _log.Error($"RemoveHomeEntry: unexpected error — {ex.Message}", ex, "OracleInventory");
            return OracleInventoryRemovalResult.Failed(ex.Message, oracleInventoryPath);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string NormalizePath(string path)
        => Path.GetFullPath(path.TrimEnd('\\', '/'))
               .ToUpperInvariant();

    private static bool IsHomeStructureComplete(string middlewareHome)
        => CompleteHomeMarkers.All(m => Directory.Exists(Path.Combine(middlewareHome, m)));

    private static void RemoveMatchingHomeElements(
        XDocument doc,
        string normalizedTargetPath,
        string listElementName,
        string homeElementName,
        List<string> removed)
    {
        var list = doc.Root?.Element(listElementName);
        if (list is null) return;

        var toRemove = list.Elements(homeElementName)
            .Where(e => NormalizePath(e.Attribute("LOC")?.Value ?? string.Empty) == normalizedTargetPath)
            .ToList();

        foreach (var el in toRemove)
        {
            removed.Add($"{listElementName}/{homeElementName}[@LOC='{el.Attribute("LOC")?.Value}']");
            el.Remove();
        }
    }
}
