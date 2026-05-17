using FluentAssertions;
using WEDM.Domain.Models;
using WEDM.Engine.OracleInventory;
using WEDM.Engine.Tests.Fakes;
using Xunit;

namespace WEDM.Engine.Tests.OracleInventory;

/// <summary>
/// Unit + integration tests for <see cref="OracleInventoryService"/>.
///
/// All tests that need an inventory.xml create a real temporary directory tree and clean
/// up on disposal.  No network access is required; all Oracle operations are tested
/// against the local filesystem.
/// </summary>
public sealed class OracleInventoryServiceTests : IDisposable
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private readonly FakeLoggingService      _log     = new();
    private readonly OracleInventoryService  _sut;
    private readonly string                  _root;   // temp dir for this test run
    private bool _disposed;

    public OracleInventoryServiceTests()
    {
        _sut  = new OracleInventoryService(_log);
        _root = Path.Combine(Path.GetTempPath(), $"WEDM_InvTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a ContentsXML/inventory.xml under the given inventory path.</summary>
    private static string CreateInventoryXml(string invPath, string xmlContent)
    {
        var dir = Path.Combine(invPath, "ContentsXML");
        Directory.CreateDirectory(dir);
        var xmlFile = Path.Combine(dir, "inventory.xml");
        File.WriteAllText(xmlFile, xmlContent);
        return xmlFile;
    }

    private static string BuildInventoryXml(params (string name, string loc)[] homes)
    {
        var homeElements = string.Join("\n", homes.Select(h =>
            $"""    <HOME NAME="{h.name}" LOC="{h.loc}" TYPE="O" IDX="1"/>"""));

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <INVENTORY>
              <VERSION_INFO>
                <SAVED_WITH>11.2.0.3.0</SAVED_WITH>
                <MINIMUM_VER>2.1.0.6.0</MINIMUM_VER>
              </VERSION_INFO>
              <HOME_LIST>
            {homeElements}
              </HOME_LIST>
            </INVENTORY>
            """;
    }

    private string MakeMwHome(string name = "MiddlewareHome") =>
        Path.Combine(_root, name);

    private string MakeInvPath(string name = "OraInventory") =>
        Path.Combine(_root, name);

    // ── ResolveInventoryXmlPath ───────────────────────────────────────────────

    [Fact]
    public void ResolveInventoryXmlPath_ReturnsNull_WhenDirectoryDoesNotExist()
    {
        var result = _sut.ResolveInventoryXmlPath(Path.Combine(_root, "nonexistent"));
        result.Should().BeNull();
    }

    [Fact]
    public void ResolveInventoryXmlPath_FindsContentsXmlFile()
    {
        var invPath = MakeInvPath();
        var xmlFile = CreateInventoryXml(invPath, BuildInventoryXml());

        var result = _sut.ResolveInventoryXmlPath(invPath);

        result.Should().Be(xmlFile);
    }

    [Fact]
    public void ResolveInventoryXmlPath_FallsBackToRootInventoryXml()
    {
        var invPath = MakeInvPath();
        Directory.CreateDirectory(invPath);
        var rootXml = Path.Combine(invPath, "inventory.xml");
        File.WriteAllText(rootXml, BuildInventoryXml());

        var result = _sut.ResolveInventoryXmlPath(invPath);

        result.Should().Be(rootXml);
    }

    // ── ReadSnapshot ─────────────────────────────────────────────────────────

    [Fact]
    public void ReadSnapshot_ReturnsNull_WhenPathIsEmpty()
    {
        _sut.ReadSnapshot(string.Empty).Should().BeNull();
    }

    [Fact]
    public void ReadSnapshot_ReturnsEmptySnapshot_WhenNoXmlExists()
    {
        var invPath = MakeInvPath();
        Directory.CreateDirectory(invPath);

        var snapshot = _sut.ReadSnapshot(invPath);

        snapshot.Should().NotBeNull();
        snapshot!.InventoryState.Should().Be(OracleCentralInventoryState.Missing);
        snapshot.InventoryHealthy.Should().BeFalse();
        snapshot.OracleHomes.Should().BeEmpty();
    }

    [Fact]
    public void ReadSnapshot_ParsesRegisteredHomes()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        var snapshot = _sut.ReadSnapshot(invPath);

        snapshot.Should().NotBeNull();
        snapshot!.OracleHomes.Should().ContainSingle(h =>
            h.Path.Equals(mwHome, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadSnapshot_ParsesMultipleHomes()
    {
        var invPath = MakeInvPath();
        var mwHome1 = MakeMwHome("MW1");
        var mwHome2 = MakeMwHome("MW2");
        CreateInventoryXml(invPath, BuildInventoryXml(
            ("OraHome1", mwHome1),
            ("OraHome2", mwHome2)));

        var snapshot = _sut.ReadSnapshot(invPath);

        snapshot!.OracleHomes.Should().HaveCount(2);
    }

    // ── IsHomeRegistered ─────────────────────────────────────────────────────

    [Fact]
    public void IsHomeRegistered_ReturnsFalse_WhenInventoryEmpty()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        CreateInventoryXml(invPath, BuildInventoryXml());

        _sut.IsHomeRegistered(mwHome, invPath).Should().BeFalse();
    }

    [Fact]
    public void IsHomeRegistered_ReturnsTrue_WhenHomeInInventory()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        _sut.IsHomeRegistered(mwHome, invPath).Should().BeTrue();
    }

    [Fact]
    public void IsHomeRegistered_IsCaseInsensitive()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        // Register with uppercase path
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome.ToUpperInvariant())));

        _sut.IsHomeRegistered(mwHome.ToLowerInvariant(), invPath).Should().BeTrue();
    }

    // ── DetectHomeState ───────────────────────────────────────────────────────

    [Fact]
    public void DetectHomeState_ReturnsClean_WhenFolderAbsentAndNotRegistered()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();   // intentionally NOT created
        CreateInventoryXml(invPath, BuildInventoryXml());

        var state = _sut.DetectHomeState(mwHome, invPath);

        state.Should().Be(OracleHomeState.Clean);
    }

    [Fact]
    public void DetectHomeState_ReturnsRegisteredAndPresent_WhenCompleteHome()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        // Create complete home markers
        Directory.CreateDirectory(Path.Combine(mwHome, "wlserver"));
        Directory.CreateDirectory(Path.Combine(mwHome, "oracle_common"));
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        var state = _sut.DetectHomeState(mwHome, invPath);

        state.Should().Be(OracleHomeState.RegisteredAndPresent);
    }

    [Fact]
    public void DetectHomeState_ReturnsRegisteredOrphaned_WhenFolderAbsent()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();  // NOT created on filesystem
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        var state = _sut.DetectHomeState(mwHome, invPath);

        state.Should().Be(OracleHomeState.RegisteredOrphaned);
    }

    [Fact]
    public void DetectHomeState_ReturnsPartialInstall_WhenOuiSubdirPresentNotRegistered()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        // Partial — has oui/ but not wlserver/ or oracle_common/
        Directory.CreateDirectory(Path.Combine(mwHome, "oui"));
        CreateInventoryXml(invPath, BuildInventoryXml());  // not registered

        var state = _sut.DetectHomeState(mwHome, invPath);

        state.Should().Be(OracleHomeState.PartialInstall);
    }

    [Fact]
    public void DetectHomeState_ReturnsInventoryLocked_WhenLockFilePresent()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        Directory.CreateDirectory(invPath);
        // Create a fresh (non-stale) lock file
        File.WriteAllText(Path.Combine(invPath, "orainventory.lock"), "LOCKED");

        var state = _sut.DetectHomeState(mwHome, invPath);

        state.Should().Be(OracleHomeState.InventoryLocked);
    }

    [Fact]
    public void DetectHomeState_ReturnsUnregisteredInstall_WhenCompleteButNotRegistered()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        Directory.CreateDirectory(Path.Combine(mwHome, "wlserver"));
        Directory.CreateDirectory(Path.Combine(mwHome, "oracle_common"));
        CreateInventoryXml(invPath, BuildInventoryXml());  // not registered

        var state = _sut.DetectHomeState(mwHome, invPath);

        state.Should().Be(OracleHomeState.UnregisteredInstall);
    }

    // ── IsPartialInstall ──────────────────────────────────────────────────────

    [Fact]
    public void IsPartialInstall_ReturnsFalse_WhenDirectoryDoesNotExist()
    {
        _sut.IsPartialInstall(MakeMwHome()).Should().BeFalse();
    }

    [Fact]
    public void IsPartialInstall_ReturnsFalse_WhenCompleteHomeMarkersPresent()
    {
        var mwHome = MakeMwHome();
        Directory.CreateDirectory(Path.Combine(mwHome, "wlserver"));
        Directory.CreateDirectory(Path.Combine(mwHome, "oracle_common"));

        _sut.IsPartialInstall(mwHome).Should().BeFalse();
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("oui")]
    [InlineData("cfgtoollogs")]
    [InlineData("jdk")]
    [InlineData("jre")]
    public void IsPartialInstall_ReturnsTrue_WhenPartialIndicatorExists(string indicator)
    {
        var mwHome = MakeMwHome($"partial_{indicator}");
        Directory.CreateDirectory(Path.Combine(mwHome, indicator));

        _sut.IsPartialInstall(mwHome).Should().BeTrue();
    }

    [Fact]
    public void IsPartialInstall_ReturnsTrue_WhenDirectoryHasAnyContent()
    {
        var mwHome = MakeMwHome("partial_files");
        Directory.CreateDirectory(mwHome);
        File.WriteAllText(Path.Combine(mwHome, "some_oracle_file.jar"), "dummy");

        _sut.IsPartialInstall(mwHome).Should().BeTrue();
    }

    // ── FindOrphanedHomes ─────────────────────────────────────────────────────

    [Fact]
    public void FindOrphanedHomes_ReturnsEmpty_WhenNoXmlExists()
    {
        var invPath = MakeInvPath();
        Directory.CreateDirectory(invPath);

        _sut.FindOrphanedHomes(invPath).Should().BeEmpty();
    }

    [Fact]
    public void FindOrphanedHomes_ReturnsOrphaned_WhenHomeMissingFromFilesystem()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();   // NOT created on filesystem
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        var orphaned = _sut.FindOrphanedHomes(invPath);

        orphaned.Should().ContainSingle(h =>
            h.Path.Equals(mwHome, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedHomes_ExcludesHomesWhoseDirectoryExists()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        Directory.CreateDirectory(mwHome);
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        _sut.FindOrphanedHomes(invPath).Should().BeEmpty();
    }

    // ── DetectLocks ───────────────────────────────────────────────────────────

    [Fact]
    public void DetectLocks_ReturnsEmpty_WhenInventoryDirDoesNotExist()
    {
        _sut.DetectLocks(Path.Combine(_root, "nonexistent_inv")).Should().BeEmpty();
    }

    [Fact]
    public void DetectLocks_ReturnsEmpty_WhenNoLockFiles()
    {
        var invPath = MakeInvPath();
        CreateInventoryXml(invPath, BuildInventoryXml());

        _sut.DetectLocks(invPath).Should().BeEmpty();
    }

    [Fact]
    public void DetectLocks_DetectsActiveLockFile()
    {
        var invPath = MakeInvPath();
        Directory.CreateDirectory(invPath);
        var lockFile = Path.Combine(invPath, "orainventory.lock");
        File.WriteAllText(lockFile, "LOCKED");

        var locks = _sut.DetectLocks(invPath);

        locks.Should().ContainSingle(l => l.LockFilePath == lockFile);
        locks[0].IsStale.Should().BeFalse();
    }

    [Fact]
    public void DetectLocks_DetectsStaleLockFile()
    {
        var invPath = MakeInvPath();
        Directory.CreateDirectory(invPath);
        var lockFile = Path.Combine(invPath, "inventory.lock");
        File.WriteAllText(lockFile, "STALE");

        // Force last-write time to > 4 hours ago
        File.SetLastWriteTimeUtc(lockFile, DateTime.UtcNow.AddHours(-5));

        var locks = _sut.DetectLocks(invPath);

        locks.Should().ContainSingle();
        locks[0].IsStale.Should().BeTrue();
    }

    [Fact]
    public void DetectLocks_DetectsLockFileInLocksSubdirectory()
    {
        var invPath  = MakeInvPath();
        var locksDir = Path.Combine(invPath, "locks");
        Directory.CreateDirectory(locksDir);
        var lockFile = Path.Combine(locksDir, "orainventory.lock");
        File.WriteAllText(lockFile, "LOCKED");

        var locks = _sut.DetectLocks(invPath);

        locks.Should().ContainSingle(l => l.LockFilePath == lockFile);
    }

    // ── ValidateForInstall ────────────────────────────────────────────────────

    [Fact]
    public void ValidateForInstall_CanProceed_WhenStateIsClean()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();   // folder absent, not registered
        CreateInventoryXml(invPath, BuildInventoryXml());

        var result = _sut.ValidateForInstall(mwHome, invPath);

        result.CanProceed.Should().BeTrue();
        result.HomeState.Should().Be(OracleHomeState.Clean);
    }

    [Fact]
    public void ValidateForInstall_Blocked_WhenHomeAlreadyRegistered()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        Directory.CreateDirectory(mwHome);
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        var result = _sut.ValidateForInstall(mwHome, invPath);

        result.CanProceed.Should().BeFalse();
        result.ConflictingHomes.Should().ContainSingle();
        result.Findings.Should().Contain(f => f.Contains("already registered"));
        result.RemediationSteps.Should().NotBeEmpty();
    }

    [Fact]
    public void ValidateForInstall_Blocked_WhenActiveLockFilePresent()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        Directory.CreateDirectory(invPath);
        File.WriteAllText(Path.Combine(invPath, "orainventory.lock"), "LOCKED");
        // No inventory.xml needed — lock takes precedence

        var result = _sut.ValidateForInstall(mwHome, invPath);

        result.CanProceed.Should().BeFalse();
        result.IsLocked.Should().BeTrue();
    }

    [Fact]
    public void ValidateForInstall_NotBlocked_ByStaleLockOnly()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();   // not created, not registered
        Directory.CreateDirectory(invPath);
        var lockFile = Path.Combine(invPath, "orainventory.lock");
        File.WriteAllText(lockFile, "STALE");
        File.SetLastWriteTimeUtc(lockFile, DateTime.UtcNow.AddHours(-5));
        CreateInventoryXml(invPath, BuildInventoryXml());

        var result = _sut.ValidateForInstall(mwHome, invPath);

        result.CanProceed.Should().BeTrue();
        result.IsLocked.Should().BeFalse();
    }

    [Fact]
    public void ValidateForInstall_Blocked_WhenPartialInstallPresent()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        Directory.CreateDirectory(Path.Combine(mwHome, "oui"));  // partial indicator
        CreateInventoryXml(invPath, BuildInventoryXml());         // not registered

        var result = _sut.ValidateForInstall(mwHome, invPath);

        result.CanProceed.Should().BeFalse();
        result.Findings.Should().Contain(f => f.Contains("partial Oracle Home artifacts"));
    }

    [Fact]
    public void ValidateForInstall_ReportsOrphanedHomes_ButDoesNotBlock()
    {
        var invPath  = MakeInvPath();
        var mwHome   = MakeMwHome();       // target — clean
        var orphaned = MakeMwHome("orphan"); // registered but no folder
        CreateInventoryXml(invPath, BuildInventoryXml(("Orphan", orphaned)));

        var result = _sut.ValidateForInstall(mwHome, invPath);

        result.CanProceed.Should().BeTrue();
        result.OrphanedHomes.Should().ContainSingle();
        result.Findings.Should().Contain(f => f.Contains("orphaned home registration"));
    }

    // ── ValidateAfterInstall ──────────────────────────────────────────────────

    [Fact]
    public void ValidateAfterInstall_CanProceed_WhenRegisteredAndComplete()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        Directory.CreateDirectory(Path.Combine(mwHome, "wlserver"));
        Directory.CreateDirectory(Path.Combine(mwHome, "oracle_common"));
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        var result = _sut.ValidateAfterInstall(mwHome, invPath);

        result.CanProceed.Should().BeTrue();
    }

    [Fact]
    public void ValidateAfterInstall_Fails_WhenMwHomeDirDoesNotExist()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();   // not created
        CreateInventoryXml(invPath, BuildInventoryXml());

        var result = _sut.ValidateAfterInstall(mwHome, invPath);

        result.CanProceed.Should().BeFalse();
        result.Findings.Should().Contain(f => f.Contains("does not exist after OUI reported success"));
    }

    [Fact]
    public void ValidateAfterInstall_StillPassesWithWarning_WhenNotInInventory()
    {
        // Some OUI versions write to local inventory only — post-install check
        // should NOT hard-fail on missing Central Inventory registration alone.
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        Directory.CreateDirectory(Path.Combine(mwHome, "wlserver"));
        Directory.CreateDirectory(Path.Combine(mwHome, "oracle_common"));
        CreateInventoryXml(invPath, BuildInventoryXml()); // not registered

        var result = _sut.ValidateAfterInstall(mwHome, invPath);

        result.CanProceed.Should().BeTrue();
        result.Findings.Should().Contain(f => f.Contains("was not found in Central Inventory"));
    }

    // ── BackupInventoryXml ────────────────────────────────────────────────────

    [Fact]
    public void BackupInventoryXml_ReturnsNull_WhenNoXmlExists()
    {
        var invPath = MakeInvPath();
        Directory.CreateDirectory(invPath);

        _sut.BackupInventoryXml(invPath).Should().BeNull();
    }

    [Fact]
    public void BackupInventoryXml_CreatesBackupFile()
    {
        var invPath = MakeInvPath();
        CreateInventoryXml(invPath, BuildInventoryXml());

        var backupPath = _sut.BackupInventoryXml(invPath);

        backupPath.Should().NotBeNullOrEmpty();
        File.Exists(backupPath).Should().BeTrue();
        backupPath.Should().Contain(".backup_");
    }

    [Fact]
    public void BackupInventoryXml_BackupContainsSameContent()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));
        var xmlPath = _sut.ResolveInventoryXmlPath(invPath)!;
        var original = File.ReadAllText(xmlPath);

        var backupPath = _sut.BackupInventoryXml(invPath)!;
        var backup     = File.ReadAllText(backupPath);

        backup.Should().Be(original);
    }

    // ── RemoveHomeEntry ───────────────────────────────────────────────────────

    [Fact]
    public void RemoveHomeEntry_ReturnsNotFound_WhenNoXmlExists()
    {
        var invPath = MakeInvPath();
        Directory.CreateDirectory(invPath);
        var mwHome = MakeMwHome();

        var result = _sut.RemoveHomeEntry(mwHome, invPath);

        result.Success.Should().BeTrue();
        result.HomeWasRegistered.Should().BeFalse();
    }

    [Fact]
    public void RemoveHomeEntry_ReturnsNotFound_WhenHomeNotInInventory()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        CreateInventoryXml(invPath, BuildInventoryXml()); // empty

        var result = _sut.RemoveHomeEntry(mwHome, invPath);

        result.Success.Should().BeTrue();
        result.HomeWasRegistered.Should().BeFalse();
    }

    [Fact]
    public void RemoveHomeEntry_RemovesRegisteredHome_AndVerifiesRemoval()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        var result = _sut.RemoveHomeEntry(mwHome, invPath);

        result.Success.Should().BeTrue();
        result.HomeWasRegistered.Should().BeTrue();
        result.RemovedEntries.Should().NotBeEmpty();
        result.BackupPath.Should().NotBeNullOrEmpty();
        File.Exists(result.BackupPath).Should().BeTrue();

        // Verify via snapshot — home must be gone
        var snapshot = _sut.ReadSnapshot(invPath);
        snapshot!.OracleHomes.Should().NotContain(h =>
            h.Path.Equals(mwHome, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemoveHomeEntry_CapturesBeforeAndAfterSnapshots()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        var result = _sut.RemoveHomeEntry(mwHome, invPath);

        result.SnapshotBefore.Should().NotBeNull();
        result.SnapshotAfter.Should().NotBeNull();
        result.SnapshotBefore!.OracleHomes.Should().ContainSingle(h =>
            h.Path.Equals(mwHome, StringComparison.OrdinalIgnoreCase));
        result.SnapshotAfter!.OracleHomes.Should().NotContain(h =>
            h.Path.Equals(mwHome, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemoveHomeEntry_IsCaseInsensitive()
    {
        var invPath  = MakeInvPath();
        var mwHome   = MakeMwHome();
        // Register with uppercase
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome.ToUpperInvariant())));

        // Remove with lowercase
        var result = _sut.RemoveHomeEntry(mwHome.ToLowerInvariant(), invPath);

        result.Success.Should().BeTrue();
        result.HomeWasRegistered.Should().BeTrue();
    }

    [Fact]
    public void RemoveHomeEntry_LeavesOtherHomesIntact()
    {
        var invPath = MakeInvPath();
        var mwHome1 = MakeMwHome("MW1");
        var mwHome2 = MakeMwHome("MW2");
        CreateInventoryXml(invPath, BuildInventoryXml(
            ("OraHome1", mwHome1),
            ("OraHome2", mwHome2)));

        _sut.RemoveHomeEntry(mwHome1, invPath);

        // mwHome2 should still be registered
        _sut.IsHomeRegistered(mwHome2, invPath).Should().BeTrue();
        _sut.IsHomeRegistered(mwHome1, invPath).Should().BeFalse();
    }

    [Fact]
    public void RemoveHomeEntry_IsIdempotent_WhenCalledTwice()
    {
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        var first  = _sut.RemoveHomeEntry(mwHome, invPath);
        var second = _sut.RemoveHomeEntry(mwHome, invPath);

        first.Success.Should().BeTrue();
        first.HomeWasRegistered.Should().BeTrue();

        second.Success.Should().BeTrue();
        second.HomeWasRegistered.Should().BeFalse(); // already gone
    }

    [Fact]
    public void RemoveHomeEntry_WritesAtomically_OriginalFileRemainsReadable()
    {
        // Even after removal the resulting XML must parse without error.
        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        _sut.RemoveHomeEntry(mwHome, invPath);

        // ReadSnapshot should not throw and should parse successfully
        var act = () => _sut.ReadSnapshot(invPath);
        act.Should().NotThrow();
    }

    // ── RollbackStep integration: RemoveMiddlewareHomeStep ────────────────────

    [Fact]
    public async Task RemoveHomeEntry_CalledByRollbackBeforeFilesystemDeletion()
    {
        // This test verifies the complete Oracle-aware rollback sequence:
        //   1. Inventory registration removed
        //   2. Re-registration check succeeds (CanProceed=true on next ValidateForInstall)

        var invPath = MakeInvPath();
        var mwHome  = MakeMwHome();
        Directory.CreateDirectory(Path.Combine(mwHome, "oui")); // partial install
        CreateInventoryXml(invPath, BuildInventoryXml(("OraHome1", mwHome)));

        // Simulate rollback: remove inventory entry
        var removal = _sut.RemoveHomeEntry(mwHome, invPath);
        removal.Success.Should().BeTrue();
        removal.HomeWasRegistered.Should().BeTrue();

        // Now delete the directory (filesystem cleanup)
        Directory.Delete(mwHome, recursive: true);

        // Post-rollback: validate for next install attempt
        var preCheck = _sut.ValidateForInstall(mwHome, invPath);

        preCheck.CanProceed.Should().BeTrue(
            "after full rollback (inventory removal + directory deletion), the state should be Clean");
        preCheck.HomeState.Should().Be(OracleHomeState.Clean);

        await Task.CompletedTask; // keeps method signature consistent for future async tests
    }
}
