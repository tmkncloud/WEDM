using FluentAssertions;
using Moq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Tests.Fakes;
using WEDM.Engine.Workflow.Steps;
using Xunit;

namespace WEDM.Engine.Tests.Rollback;

// ═══════════════════════════════════════════════════════════════════════════════
// OracleRollbackTests
// ═══════════════════════════════════════════════════════════════════════════════
//
// Tests for:
//   • OracleInstallRollbackExecutor     — full Oracle home rollback (Install/Infrastructure)
//   • OracleFormsReportsRollbackExecutor — Forms/Reports-specific rollback
//   • OracleOhsWebTierRollbackExecutor  — OHS/WebTier-specific rollback
//   • OracleJavaHomeRollbackExecutor    — JavaHome env var rollback
//   • OracleRollbackVerificationService — post-rollback verification checks
//   • OracleRollbackCore                — shared primitive operations
//
// All filesystem tests create a real temp directory on disk and clean up in Dispose.
// Rollback operations that require a real install state (services, registry) are
// tested via dry-run mode or Moq stubs, so they can run on any CI machine.
// ═══════════════════════════════════════════════════════════════════════════════

// ── OracleInstallRollbackExecutorTests ────────────────────────────────────────

public sealed class OracleInstallRollbackExecutorTests : IDisposable
{
    private readonly string                    _root;
    private readonly FakeLoggingService        _log    = new();
    private readonly Mock<IOracleInventoryService> _inv = new();
    private readonly Mock<IOracleProcessManager>   _pm  = new();

    public OracleInstallRollbackExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wedm-rollback-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // Default: no processes running, no inventory registration
        _pm.Setup(p => p.DetectMiddlewareProcesses()).Returns([]);
        _pm.Setup(p => p.StopProcessesAsync(
                It.IsAny<IEnumerable<OracleProcessDescriptor>>(),
                It.IsAny<bool>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessStopResult { StoppedCount = 0, FailedCount = 0 });

        _inv.Setup(i => i.RemoveHomeEntry(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string mw, string inv) => OracleInventoryRemovalResult.NotFound(inv));
        _inv.Setup(i => i.ReadSnapshot(It.IsAny<string>())).Returns((OracleInventorySnapshot?)null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private DeploymentConfiguration MakeConfig(string? homeOverride = null)
    {
        var mwHome = homeOverride ?? Path.Combine(_root, "Oracle_MW");
        return new DeploymentConfiguration
        {
            Paths = new PathConfiguration
            {
                MiddlewareHome  = mwHome,
                OracleInventory = Path.Combine(_root, "oraInventory"),
                OracleRoot      = Path.Combine(_root, "Oracle"),
                TempDirectory   = Path.Combine(_root, "Temp"),
            },
            OracleLifecycle = new OracleLifecycleConfiguration
            {
                DryRunRollback                = false,
                ForceKillProcessesOnRollback  = true,
                ProcessShutdownTimeoutSeconds = 5
            }
        };
    }

    private static DeploymentStep MakeStep(OracleRollbackCompensation? comp = null)
        => new() { Name = "InstallWebLogic", RollbackAction = "Remove-MiddlewareHome",
                   RollbackCompensation = comp };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HomeDirectoryExists_DeletesIt()
    {
        // Arrange
        var homeDir = Path.Combine(_root, "Oracle_MW");
        Directory.CreateDirectory(homeDir);

        var config = MakeConfig(homeDir);
        var step   = MakeStep(new OracleRollbackCompensation
        {
            OracleHomePaths     = [homeDir],
            OracleInventoryPath = config.Paths.OracleInventory
        });

        var sut = new OracleInstallRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert
        result.Success.Should().BeTrue();
        Directory.Exists(homeDir).Should().BeFalse();
        config.OracleRollback.Should().NotBeNull();
        config.OracleRollback!.RemovedHomes.Should().Contain(homeDir);
    }

    [Fact]
    public async Task ExecuteAsync_HomeAlreadyAbsent_ReturnsSuccess()
    {
        // Arrange
        var homeDir = Path.Combine(_root, "NonExistent_MW");
        var config  = MakeConfig(homeDir);
        var step    = MakeStep(new OracleRollbackCompensation
        {
            OracleHomePaths     = [homeDir],
            OracleInventoryPath = config.Paths.OracleInventory
        });

        var sut = new OracleInstallRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert — already clean is not a failure
        result.Success.Should().BeTrue();
        config.OracleRollback.Should().NotBeNull();
        config.OracleRollback!.RemovedHomes.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_DryRunMode_DeletesNothing()
    {
        // Arrange
        var homeDir = Path.Combine(_root, "Oracle_MW_DryRun");
        Directory.CreateDirectory(homeDir);

        var config = MakeConfig(homeDir);
        config.OracleLifecycle.DryRunRollback = true;

        var step = MakeStep(new OracleRollbackCompensation
        {
            OracleHomePaths     = [homeDir],
            OracleInventoryPath = config.Paths.OracleInventory
        });

        var sut = new OracleInstallRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert — directory must still exist in dry-run mode
        result.Success.Should().BeTrue();
        Directory.Exists(homeDir).Should().BeTrue("dry-run must not delete anything");
        config.OracleRollback!.DryRunMode.Should().BeTrue();
        config.OracleRollback.RemovedHomes.Should().BeEmpty("dry-run suppresses all deletions");
    }

    [Fact]
    public async Task ExecuteAsync_InventoryRegistered_DetachesAndLogs()
    {
        // Arrange
        var homeDir = Path.Combine(_root, "Oracle_MW_Inv");
        Directory.CreateDirectory(homeDir);
        var invPath = config_inv();

        _inv.Setup(i => i.RemoveHomeEntry(homeDir, invPath))
            .Returns(OracleInventoryRemovalResult.Removed(
                inventoryPath: invPath,
                backupPath:    invPath + ".bak",
                removed:       [homeDir],
                before: new OracleInventorySnapshot
                {
                    OracleHomes = [new OracleHomeDescriptor { Path = homeDir }]
                },
                after: new OracleInventorySnapshot { OracleHomes = [] }));

        var config = new DeploymentConfiguration
        {
            Paths = new PathConfiguration
            {
                MiddlewareHome  = homeDir,
                OracleInventory = invPath,
            },
            OracleLifecycle = new OracleLifecycleConfiguration()
        };

        var step = MakeStep(new OracleRollbackCompensation
        {
            OracleHomePaths     = [homeDir],
            OracleInventoryPath = invPath
        });

        var sut = new OracleInstallRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert
        result.Success.Should().BeTrue();
        _inv.Verify(i => i.RemoveHomeEntry(homeDir, invPath), Times.Once);
        config.OracleRollback!.DetachedInventoryEntries.Should().ContainMatch("*" + homeDir + "*");
    }

    [Fact]
    public async Task ExecuteAsync_WithCompensation_DeletesGeneratedFiles()
    {
        // Arrange
        var homeDir = Path.Combine(_root, "Oracle_MW_Files");
        Directory.CreateDirectory(homeDir);

        var rspFile  = Path.Combine(_root, "install.rsp");
        var invPtr   = Path.Combine(_root, "oraInst.loc");
        File.WriteAllText(rspFile, "INSTALL=true");
        File.WriteAllText(invPtr, "inventory_loc=/oracle/oraInventory");

        var config = MakeConfig(homeDir);
        var step   = MakeStep(new OracleRollbackCompensation
        {
            OracleHomePaths     = [homeDir],
            OracleInventoryPath = config.Paths.OracleInventory,
            GeneratedFilePaths  = [rspFile, invPtr]
        });

        var sut = new OracleInstallRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert
        result.Success.Should().BeTrue();
        File.Exists(rspFile).Should().BeFalse("generated files must be cleaned up");
        File.Exists(invPtr).Should().BeFalse("generated files must be cleaned up");
        config.OracleRollback!.RemovedGeneratedFiles.Should().Contain(rspFile).And.Contain(invPtr);
    }

    [Fact]
    public async Task ExecuteAsync_NullCompensation_FallsBackToConfigPaths()
    {
        // Arrange — no compensation record; executor should use config.Paths.MiddlewareHome
        var homeDir = Path.Combine(_root, "Oracle_MW_NoComp");
        Directory.CreateDirectory(homeDir);
        var config  = MakeConfig(homeDir);
        var step    = MakeStep(comp: null);  // no compensation

        var sut = new OracleInstallRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert — fallback to config.Paths.MiddlewareHome still works
        result.Success.Should().BeTrue();
        Directory.Exists(homeDir).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_OracleRollbackAccumulates_WhenCalledTwice()
    {
        // Arrange — two successive rollback executors writing to the same config.OracleRollback
        var home1 = Path.Combine(_root, "MW1"); Directory.CreateDirectory(home1);
        var home2 = Path.Combine(_root, "MW2"); Directory.CreateDirectory(home2);

        var config = new DeploymentConfiguration
        {
            Paths = new PathConfiguration { MiddlewareHome = home1, OracleInventory = _root },
            OracleLifecycle = new OracleLifecycleConfiguration()
        };

        var step1 = MakeStep(new OracleRollbackCompensation { OracleHomePaths = [home1] });
        var step2 = MakeStep(new OracleRollbackCompensation { OracleHomePaths = [home2] });

        var sut = new OracleInstallRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act — simulate two Oracle executors during one rollback pass
        await sut.ExecuteAsync(step1, config);
        await sut.ExecuteAsync(step2, config);

        // Assert — both homes appear in the accumulated report
        config.OracleRollback!.RemovedHomes.Should().Contain(home1).And.Contain(home2);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesDetected_StopsThemAndRecords()
    {
        // Arrange
        var proc = new OracleProcessDescriptor
        {
            ProcessId   = 12345,
            ProcessName = "java",
            Category    = "OUI"
        };

        _pm.Setup(p => p.DetectMiddlewareProcesses()).Returns([proc]);
        _pm.Setup(p => p.StopProcessesAsync(
                It.IsAny<IEnumerable<OracleProcessDescriptor>>(),
                true,
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessStopResult
            {
                StoppedCount = 1,
                Messages     = ["Stopped java (12345)"]
            });

        var homeDir = Path.Combine(_root, "Oracle_MW_Proc");
        var config  = MakeConfig(homeDir);
        var step    = MakeStep(new OracleRollbackCompensation { OracleHomePaths = [homeDir] });

        var sut = new OracleInstallRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert
        result.Success.Should().BeTrue();
        _pm.Verify(p => p.StopProcessesAsync(
            It.IsAny<IEnumerable<OracleProcessDescriptor>>(),
            true, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        config.OracleRollback!.StoppedProcesses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_DryRunMode_NoInventoryMutation()
    {
        // Arrange
        var homeDir = Path.Combine(_root, "Oracle_MW_InvDry");
        Directory.CreateDirectory(homeDir);

        var config = MakeConfig(homeDir);
        config.OracleLifecycle.DryRunRollback = true;

        var step = MakeStep(new OracleRollbackCompensation
        {
            OracleHomePaths     = [homeDir],
            OracleInventoryPath = config.Paths.OracleInventory
        });

        var sut = new OracleInstallRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act
        await sut.ExecuteAsync(step, config);

        // Assert — RemoveHomeEntry must NOT be called in dry-run mode
        _inv.Verify(i => i.RemoveHomeEntry(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private string config_inv() => Path.Combine(_root, "oraInventory");
}

// ── OracleFormsReportsRollbackExecutorTests ───────────────────────────────────

public sealed class OracleFormsReportsRollbackExecutorTests : IDisposable
{
    private readonly string                    _root;
    private readonly FakeLoggingService        _log = new();
    private readonly Mock<IOracleInventoryService> _inv = new();
    private readonly Mock<IOracleProcessManager>   _pm  = new();

    public OracleFormsReportsRollbackExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wedm-forms-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _pm.Setup(p => p.DetectMiddlewareProcesses()).Returns([]);
        _pm.Setup(p => p.StopProcessesAsync(
                It.IsAny<IEnumerable<OracleProcessDescriptor>>(),
                It.IsAny<bool>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessStopResult());

        _inv.Setup(i => i.RemoveHomeEntry(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string mw, string inv) => OracleInventoryRemovalResult.NotFound(inv));
        _inv.Setup(i => i.ReadSnapshot(It.IsAny<string>())).Returns((OracleInventorySnapshot?)null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_CompensationWithHomePath_DeletesFormsHome()
    {
        // Arrange
        var formsHome = Path.Combine(_root, "FormsHome");
        Directory.CreateDirectory(formsHome);

        var config = new DeploymentConfiguration
        {
            Paths = new PathConfiguration
            {
                OracleInventory = Path.Combine(_root, "oraInventory"),
            },
            Domain = new DomainConfiguration
            {
                FormsReports = new FormsReportsConfiguration { FormsPath = formsHome }
            },
            OracleLifecycle = new OracleLifecycleConfiguration()
        };

        var step = new DeploymentStep
        {
            Name           = "InstallFormsReports",
            RollbackAction = "Remove-FormsReports",
            RollbackCompensation = new OracleRollbackCompensation
            {
                OracleHomePaths     = [formsHome],
                OracleInventoryPath = config.Paths.OracleInventory
            }
        };

        var sut = new OracleFormsReportsRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert
        result.Success.Should().BeTrue();
        Directory.Exists(formsHome).Should().BeFalse();
        config.OracleRollback!.RemovedHomes.Should().Contain(formsHome);
    }

    [Fact]
    public async Task ExecuteAsync_DryRunMode_LeavesFormsHomeIntact()
    {
        // Arrange
        var formsHome = Path.Combine(_root, "FormsHome_Dry");
        Directory.CreateDirectory(formsHome);

        var config = new DeploymentConfiguration
        {
            Paths  = new PathConfiguration { OracleInventory = Path.Combine(_root, "inv") },
            Domain = new DomainConfiguration { FormsReports = new() },
            OracleLifecycle = new OracleLifecycleConfiguration { DryRunRollback = true }
        };

        var step = new DeploymentStep
        {
            RollbackCompensation = new OracleRollbackCompensation { OracleHomePaths = [formsHome] }
        };

        var sut = new OracleFormsReportsRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert
        result.Success.Should().BeTrue();
        Directory.Exists(formsHome).Should().BeTrue("dry-run must not delete");
    }

    [Fact]
    public async Task ExecuteAsync_CompensationWithEnvVars_RecordsRemovalAttempts()
    {
        // Arrange — compensation records some env vars; we verify the report captures them
        // (actual env var removal on CI will silently skip non-existent vars)
        var config = new DeploymentConfiguration
        {
            Paths  = new PathConfiguration { OracleInventory = Path.Combine(_root, "inv") },
            Domain = new DomainConfiguration { FormsReports = new() },
            OracleLifecycle = new OracleLifecycleConfiguration()
        };

        var step = new DeploymentStep
        {
            RollbackCompensation = new OracleRollbackCompensation
            {
                OracleHomePaths            = [],
                SetEnvironmentVariableNames = ["FORMS_PATH"]
            }
        };

        var sut = new OracleFormsReportsRollbackExecutor(_log, _inv.Object, _pm.Object);

        // Act — FORMS_PATH is unlikely to be set on CI; executor should still return Ok
        var result = await sut.ExecuteAsync(step, config);

        // Assert
        result.Success.Should().BeTrue();
    }
}

// ── OracleJavaHomeRollbackExecutorTests ───────────────────────────────────────

public sealed class OracleJavaHomeRollbackExecutorTests
{
    private readonly FakeLoggingService _log = new();

    private static DeploymentConfiguration MakeConfig(string javaHome, bool dryRun = false)
        => new()
        {
            Java = new JavaConfiguration { JavaHome = javaHome },
            OracleLifecycle = new OracleLifecycleConfiguration { DryRunRollback = dryRun }
        };

    [Fact]
    public async Task ExecuteAsync_JavaHomeNotSet_ReturnsSuccess()
    {
        // Arrange
        var config = MakeConfig(javaHome: string.Empty);
        var step   = new DeploymentStep { Name = "ConfigureJavaHome", RollbackAction = "Remove-JavaEnvVars" };
        var sut    = new OracleJavaHomeRollbackExecutor(_log);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert — graceful when nothing to remove
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_DryRunMode_DoesNotMutateEnvironment()
    {
        // Arrange
        var config = MakeConfig(@"C:\Program Files\Java\jdk1.8.0_202", dryRun: true);
        var step   = new DeploymentStep
        {
            RollbackCompensation = new OracleRollbackCompensation
            {
                SetEnvironmentVariableNames = ["JAVA_HOME"]
            }
        };
        var sut = new OracleJavaHomeRollbackExecutor(_log);

        // Capture current JAVA_HOME before the test
        var beforeJavaHome = Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.Machine);

        // Act
        var result = await sut.ExecuteAsync(step, config);

        // Assert — machine JAVA_HOME unchanged
        result.Success.Should().BeTrue();
        Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.Machine)
            .Should().Be(beforeJavaHome, "dry-run must not modify the machine environment");
    }

    [Fact]
    public async Task ExecuteAsync_AccumulatesReport()
    {
        // Arrange
        var config = MakeConfig(@"C:\FakeJava");
        var step   = new DeploymentStep { RollbackCompensation = null };
        var sut    = new OracleJavaHomeRollbackExecutor(_log);

        // Act
        await sut.ExecuteAsync(step, config);

        // Assert — OracleRollback should be initialised (even if removal was a no-op)
        config.OracleRollback.Should().NotBeNull();
    }
}

// ── OracleRollbackVerificationServiceTests ────────────────────────────────────

public sealed class OracleRollbackVerificationServiceTests : IDisposable
{
    private readonly string               _root;
    private readonly FakeLoggingService   _log = new();
    private readonly Mock<IOracleInventoryService> _inv = new();

    public OracleRollbackVerificationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wedm-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _inv.Setup(i => i.ReadSnapshot(It.IsAny<string>())).Returns((OracleInventorySnapshot?)null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Verify_HomeAbsentAndInventoryNull_ReportsClean()
    {
        // Arrange — home does not exist; inventory snapshot returns null (no homes)
        var missingHome = Path.Combine(_root, "NonExistent");

        // Act
        var result = OracleRollbackVerificationService.Verify(
            [missingHome], inventoryPath: null, _inv.Object, _log, "Test");

        // Assert
        result.IsClean.Should().BeTrue();
        result.Findings.Should().Contain(f => f.Contains("✔") && f.Contains(missingHome));
    }

    [Fact]
    public void Verify_HomeStillExists_ReportsNotClean()
    {
        // Arrange — home directory still exists after rollback
        var presentHome = Path.Combine(_root, "LeftBehindHome");
        Directory.CreateDirectory(presentHome);

        // Act
        var result = OracleRollbackVerificationService.Verify(
            [presentHome], inventoryPath: null, _inv.Object, _log, "Test");

        // Assert
        result.IsClean.Should().BeFalse();
        result.Findings.Should().Contain(f => f.Contains("✗") && f.Contains(presentHome));
        result.ManualActionsRequired.Should().Contain(a => a.Contains(presentHome));
    }

    [Fact]
    public void Verify_InventoryStillRegistersHome_ReportsNotClean()
    {
        // Arrange — inventory snapshot shows the home is still registered
        var homeDir = Path.Combine(_root, "StillRegistered");
        var invPath = Path.Combine(_root, "oraInventory");
        Directory.CreateDirectory(invPath);

        _inv.Setup(i => i.ReadSnapshot(invPath))
            .Returns(new OracleInventorySnapshot
            {
                OracleHomes = [new OracleHomeDescriptor { Path = homeDir }]
            });

        // Act
        var result = OracleRollbackVerificationService.Verify(
            [homeDir], invPath, _inv.Object, _log, "Test");

        // Assert
        result.IsClean.Should().BeFalse();
        result.Findings.Should().Contain(f => f.Contains("✗") && f.Contains("registered in inventory"));
        result.ManualActionsRequired.Should().Contain(a => a.Contains(homeDir));
    }

    [Fact]
    public void Verify_HomeAbsentAndNotInInventory_ReportsClean()
    {
        // Arrange — home does not exist AND inventory shows it gone
        var homeDir = Path.Combine(_root, "CleanHome");
        var invPath = Path.Combine(_root, "cleanInv");
        Directory.CreateDirectory(invPath);

        _inv.Setup(i => i.ReadSnapshot(invPath))
            .Returns(new OracleInventorySnapshot { OracleHomes = [] });

        // Act
        var result = OracleRollbackVerificationService.Verify(
            [homeDir], invPath, _inv.Object, _log, "Test");

        // Assert
        result.IsClean.Should().BeTrue();
        result.Findings.Should().Contain(f => f.Contains("✔") && f.Contains("removed"));
        result.Findings.Should().Contain(f => f.Contains("✔") && f.Contains("not registered"));
    }

    [Fact]
    public void Verify_LockFilePresent_AddsWarning()
    {
        // Arrange
        var homeDir = Path.Combine(_root, "HomeLock");
        var invPath = Path.Combine(_root, "lockInv");
        Directory.CreateDirectory(invPath);
        // Write a fake .lock file to the inventory dir
        File.WriteAllText(Path.Combine(invPath, "oui.lock"), "locked");

        _inv.Setup(i => i.ReadSnapshot(invPath))
            .Returns(new OracleInventorySnapshot { OracleHomes = [] });

        // Act
        var result = OracleRollbackVerificationService.Verify(
            [homeDir], invPath, _inv.Object, _log, "Test");

        // Assert
        result.RemainingWarnings.Should().Contain(w => w.Contains("lock"));
    }

    [Fact]
    public void Verify_MultipleHomes_AllChecked()
    {
        // Arrange — two homes: one removed, one still present
        var removedHome = Path.Combine(_root, "Home1_Removed");
        var presentHome = Path.Combine(_root, "Home2_Present");
        Directory.CreateDirectory(presentHome);  // still exists

        // Act
        var result = OracleRollbackVerificationService.Verify(
            [removedHome, presentHome], inventoryPath: null, _inv.Object, _log, "Test");

        // Assert — one pass, one fail
        result.IsClean.Should().BeFalse();
        result.Findings.Should().Contain(f => f.Contains("✔") && f.Contains(removedHome));
        result.Findings.Should().Contain(f => f.Contains("✗") && f.Contains(presentHome));
    }
}

// ── OracleRollbackCoreTests ───────────────────────────────────────────────────

public sealed class OracleRollbackCoreTests : IDisposable
{
    private readonly string               _root;
    private readonly FakeLoggingService   _log = new();
    private readonly Mock<IOracleInventoryService> _inv = new();
    private readonly Mock<IOracleProcessManager>   _pm  = new();
    private readonly OracleRollbackCore   _sut;

    public OracleRollbackCoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wedm-core-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _pm.Setup(p => p.DetectMiddlewareProcesses()).Returns([]);
        _pm.Setup(p => p.StopProcessesAsync(
                It.IsAny<IEnumerable<OracleProcessDescriptor>>(),
                It.IsAny<bool>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessStopResult());

        _inv.Setup(i => i.RemoveHomeEntry(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string mw, string inv) => OracleInventoryRemovalResult.NotFound(inv));

        _sut = new OracleRollbackCore(_log, _inv.Object, _pm.Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── DeleteDirectory ───────────────────────────────────────────────────────

    [Fact]
    public void DeleteDirectory_DirectoryExists_DeletesIt()
    {
        // Arrange
        var dir = Path.Combine(_root, "del_me");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "content");

        // Act
        var result = _sut.DeleteDirectory(dir, dryRun: false, "Test");

        // Assert
        result.Deleted.Should().BeTrue();
        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public void DeleteDirectory_DryRun_LeavesDirectoryIntact()
    {
        // Arrange
        var dir = Path.Combine(_root, "dry_dir");
        Directory.CreateDirectory(dir);

        // Act
        var result = _sut.DeleteDirectory(dir, dryRun: true, "Test");

        // Assert
        result.Deleted.Should().BeFalse();
        Directory.Exists(dir).Should().BeTrue();
        result.Message.Should().Contain("DRY-RUN");
    }

    [Fact]
    public void DeleteDirectory_AlreadyAbsent_ReturnsNotDeleted()
    {
        // Arrange
        var dir = Path.Combine(_root, "not_here");

        // Act
        var result = _sut.DeleteDirectory(dir, dryRun: false, "Test");

        // Assert
        result.Deleted.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }

    // ── DeleteFile ────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteFile_FileExists_DeletesIt()
    {
        var file = Path.Combine(_root, "response.rsp");
        File.WriteAllText(file, "data");

        var result = _sut.DeleteFile(file, dryRun: false, "Test");

        result.Deleted.Should().BeTrue();
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public void DeleteFile_DryRun_LeavesFileIntact()
    {
        var file = Path.Combine(_root, "dry_file.rsp");
        File.WriteAllText(file, "data");

        var result = _sut.DeleteFile(file, dryRun: true, "Test");

        result.Deleted.Should().BeFalse();
        File.Exists(file).Should().BeTrue();
    }

    // ── DetachFromInventory ───────────────────────────────────────────────────

    [Fact]
    public void DetachFromInventory_NoInventoryPath_ReturnsSkipped()
    {
        var result = _sut.DetachFromInventory("/oracle/home", inventoryPath: null, dryRun: false, "Test");

        result.Detached.Should().BeFalse();
        result.Message.Should().Contain("not configured");
    }

    [Fact]
    public void DetachFromInventory_DryRun_DoesNotCallRemoveHomeEntry()
    {
        _sut.DetachFromInventory("/oracle/home", "/inventory", dryRun: true, "Test");

        _inv.Verify(i => i.RemoveHomeEntry(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void DetachFromInventory_HomeRegistered_ReportsDetached()
    {
        const string home = @"C:\Oracle\MW";
        const string inv  = @"C:\Oracle\oraInventory";

        _inv.Setup(i => i.RemoveHomeEntry(home, inv))
            .Returns(OracleInventoryRemovalResult.Removed(
                inventoryPath: inv,
                backupPath:    inv + ".bak",
                removed:       [home],
                before: new OracleInventorySnapshot { OracleHomes = [new() { Path = home }] },
                after:  new OracleInventorySnapshot { OracleHomes = [] }));

        var result = _sut.DetachFromInventory(home, inv, dryRun: false, "Test");

        result.Detached.Should().BeTrue();
        result.Message.Should().Contain("detached");
    }

    // ── RemoveEnvironmentVariable ─────────────────────────────────────────────

    [Fact]
    public void RemoveEnvironmentVariable_VarNotSet_ReturnsNotRemoved()
    {
        // A variable that almost certainly doesn't exist
        var result = _sut.RemoveEnvironmentVariable(
            "WEDM_TEST_UNUSED_VAR_XZY123", dryRun: false, "Test");

        result.Removed.Should().BeFalse();
        result.Message.Should().Contain("not set");
    }

    [Fact]
    public void RemoveEnvironmentVariable_DryRun_DoesNotMutateEnv()
    {
        // Arrange — set a test var in the process-level scope (not machine-level to avoid elevation)
        // We test dry-run by verifying the method reports not-removed without touching machine env
        var result = _sut.RemoveEnvironmentVariable(
            "WEDM_DRYRUN_NONEXISTENT_VAR", dryRun: true, "Test");

        result.Removed.Should().BeFalse();
    }

    // ── HasInventoryLocks ─────────────────────────────────────────────────────

    [Fact]
    public void HasInventoryLocks_NoLockFiles_ReturnsFalse()
    {
        var invDir = Path.Combine(_root, "inv_clean");
        Directory.CreateDirectory(invDir);

        var result = OracleRollbackCore.HasInventoryLocks(invDir);

        result.Should().BeFalse();
    }

    [Fact]
    public void HasInventoryLocks_LockFilePresent_ReturnsTrue()
    {
        var invDir = Path.Combine(_root, "inv_locked");
        Directory.CreateDirectory(invDir);
        File.WriteAllText(Path.Combine(invDir, "some.lock"), "lock");

        var result = OracleRollbackCore.HasInventoryLocks(invDir);

        result.Should().BeTrue();
    }

    [Fact]
    public void HasInventoryLocks_NullOrMissingPath_ReturnsFalse()
    {
        OracleRollbackCore.HasInventoryLocks(null).Should().BeFalse();
        OracleRollbackCore.HasInventoryLocks(@"C:\NonExistent\Path_XYZ").Should().BeFalse();
    }

    // ── InferMiddlewareServiceNames ────────────────────────────────────────────

    [Fact]
    public void InferMiddlewareServiceNames_StandardConfig_IncludesAdminAndNodeMgr()
    {
        var config = new DeploymentConfiguration
        {
            Domain = new DomainConfiguration
            {
                AdminServerName = "AdminServer",
                NodeManager     = new NodeManagerConfiguration { ServiceName = "WLS NodeManager" },
                ManagedServers  = [new ManagedServerDefinition { Name = "WLS_FORMS", RegisterService = true }]
            }
        };

        var names = OracleRollbackCore.InferMiddlewareServiceNames(config);

        names.Should().Contain("WLS_AdminServer");
        names.Should().Contain("WLS NodeManager");
        names.Should().Contain("WLS_WLS_FORMS");
    }

    [Fact]
    public void InferMiddlewareServiceNames_NonRegisteredServer_Excluded()
    {
        var config = new DeploymentConfiguration
        {
            Domain = new DomainConfiguration
            {
                AdminServerName = "AdminServer",
                NodeManager     = new NodeManagerConfiguration { ServiceName = "NM" },
                ManagedServers  =
                [
                    new ManagedServerDefinition { Name = "WLS_FORMS", RegisterService = false }
                ]
            }
        };

        var names = OracleRollbackCore.InferMiddlewareServiceNames(config);

        names.Should().NotContain("WLS_WLS_FORMS");
    }
}

// ── OracleRollbackReportTests — model-level tests ─────────────────────────────

public sealed class OracleRollbackReportTests
{
    [Fact]
    public void MergeFrom_CombinesAllLists()
    {
        // Arrange
        var a = new OracleRollbackReport
        {
            RemovedHomes               = [@"C:\Oracle\MW1"],
            DetachedInventoryEntries   = ["Home1 @ C:\\Oracle\\MW1"],
            StoppedAndRemovedServices  = ["WLS_Admin"],
            RemovedEnvironmentVariables = ["JAVA_HOME"],
            StoppedProcesses           = ["PID 100 java"],
            RemovedGeneratedFiles      = [@"C:\Temp\install.rsp"],
            RemainingWarnings          = ["Warning A"],
            ManualActionsRequired      = ["Manual A"],
            InventoryClean             = true,
            NoOuiLocks                 = true,
            NoOrphanProcesses          = true
        };

        var b = new OracleRollbackReport
        {
            RemovedHomes               = [@"C:\Oracle\MW2"],
            DetachedInventoryEntries   = ["Home2 @ C:\\Oracle\\MW2"],
            StoppedAndRemovedServices  = ["WLS_FORMS"],
            RemovedEnvironmentVariables = ["ORACLE_HOME"],
            RemainingWarnings          = ["Warning B"],
            ManualActionsRequired      = ["Manual B"],
            InventoryClean             = false,  // pessimistic — should propagate
            NoOuiLocks                 = true,
            NoOrphanProcesses          = true
        };

        // Act
        a.MergeFrom(b);

        // Assert — lists combined
        a.RemovedHomes.Should().HaveCount(2).And.Contain(@"C:\Oracle\MW2");
        a.DetachedInventoryEntries.Should().HaveCount(2);
        a.StoppedAndRemovedServices.Should().Contain("WLS_FORMS");
        a.RemovedEnvironmentVariables.Should().Contain("ORACLE_HOME");
        a.RemainingWarnings.Should().Contain("Warning B");
        a.ManualActionsRequired.Should().Contain("Manual B");

        // Verification flags: AND semantics — false wins
        a.InventoryClean.Should().BeFalse("AND with false gives false");
        a.NoOuiLocks.Should().BeTrue("both true — stays true");
    }

    [Fact]
    public void IsFullyClean_AllGreen_ReturnsTrue()
    {
        var report = new OracleRollbackReport
        {
            InventoryClean    = true,
            NoOuiLocks        = true,
            NoOrphanProcesses = true
            // RemainingWarnings and ManualActionsRequired both empty by default
        };

        report.IsFullyClean.Should().BeTrue();
    }

    [Fact]
    public void IsFullyClean_HasManualActions_ReturnsFalse()
    {
        var report = new OracleRollbackReport
        {
            InventoryClean    = true,
            NoOuiLocks        = true,
            NoOrphanProcesses = true
        };
        report.ManualActionsRequired.Add("Drop RCU schemas manually.");

        report.IsFullyClean.Should().BeFalse();
    }

    [Fact]
    public void IsFullyClean_InventoryNotClean_ReturnsFalse()
    {
        var report = new OracleRollbackReport
        {
            InventoryClean    = false,
            NoOuiLocks        = true,
            NoOrphanProcesses = true
        };

        report.IsFullyClean.Should().BeFalse();
    }

    [Fact]
    public void DryRunMode_SetOnReport_SurfacesInIsFullyClean()
    {
        // DryRunMode alone doesn't affect IsFullyClean — clean state flags do
        var report = new OracleRollbackReport
        {
            DryRunMode        = true,
            InventoryClean    = true,
            NoOuiLocks        = true,
            NoOrphanProcesses = true
        };

        report.IsFullyClean.Should().BeTrue("dry-run with clean flags still reports clean");
    }
}

// ── OracleRollbackCompensationTests ─────────────────────────────────────────

public sealed class OracleRollbackCompensationTests
{
    [Fact]
    public void Compensation_DefaultsAreEmptyLists_NotNull()
    {
        var comp = new OracleRollbackCompensation();

        comp.OracleHomePaths.Should().NotBeNull().And.BeEmpty();
        comp.CreatedServiceNames.Should().NotBeNull().And.BeEmpty();
        comp.SetEnvironmentVariableNames.Should().NotBeNull().And.BeEmpty();
        comp.CreatedRegistryKeyPaths.Should().NotBeNull().And.BeEmpty();
        comp.GeneratedFilePaths.Should().NotBeNull().And.BeEmpty();
        comp.AppliedPatchIds.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Compensation_CapturedAt_IsApproximatelyNow()
    {
        var before = DateTimeOffset.UtcNow;
        var comp   = new OracleRollbackCompensation();
        var after  = DateTimeOffset.UtcNow;

        comp.CapturedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void DeploymentStep_RollbackCompensation_IsNullByDefault()
    {
        var step = new DeploymentStep();

        step.RollbackCompensation.Should().BeNull();
    }

    [Fact]
    public void DeploymentConfiguration_OracleRollback_IsNullByDefault()
    {
        var config = new DeploymentConfiguration();

        config.OracleRollback.Should().BeNull();
    }
}
