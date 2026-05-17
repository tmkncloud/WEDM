using FluentAssertions;
using Moq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Decommissioning;
using WEDM.Engine.Installer;
using WEDM.Engine.ResponseFiles;
using WEDM.Engine.Tests.Fakes;
using Xunit;

namespace WEDM.Engine.Tests.Installer;

/// <summary>
/// Unit + integration tests for:
///   • <see cref="InstallerFailureClassifier"/> — exit-code and signal-string classification
///   • <see cref="InstallerRetryPreflight"/> — all five environment checks
///   • <see cref="InstallRetryIsolationService"/> — context building, retry isolation, preflight delegation
///
/// All tests that touch the filesystem create a real temp directory tree and clean up on Dispose.
/// </summary>
public sealed class InstallerRetryIsolationTests : IDisposable
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private readonly string                    _root;
    private readonly FakeLoggingService        _log;
    private readonly Mock<IOracleInventoryService> _inventoryMock;
    private readonly Mock<IOracleCleanupService>   _cleanupMock;
    private readonly ResponseFileGenerator     _rspGen;
    private readonly InstallRetryIsolationService _sut;
    private bool _disposed;

    public InstallerRetryIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"WEDM_RetryTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _log           = new FakeLoggingService();
        _inventoryMock = new Mock<IOracleInventoryService>(MockBehavior.Loose);
        _cleanupMock   = new Mock<IOracleCleanupService>(MockBehavior.Loose);
        _rspGen        = new ResponseFileGenerator(_log);

        // Safe defaults — inventory is clean, no locks
        _inventoryMock
            .Setup(s => s.DetectHomeState(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(OracleHomeState.Clean);
        _inventoryMock
            .Setup(s => s.DetectLocks(It.IsAny<string>()))
            .Returns(Array.Empty<OracleInventoryLockDescriptor>().ToList().AsReadOnly());

        _sut = new InstallRetryIsolationService(
            _log,
            _rspGen,
            _cleanupMock.Object,
            _inventoryMock.Object);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private DeploymentConfiguration MakeConfig(string? tempDir = null)
    {
        var t = tempDir ?? Path.Combine(_root, "temp");
        Directory.CreateDirectory(t);
        return new DeploymentConfiguration
        {
            Paths = new PathConfiguration
            {
                TempDirectory  = t,
                MiddlewareHome  = Path.Combine(_root, "mw"),
                OracleInventory = Path.Combine(_root, "inv"),
                OracleRoot      = _root,
            },
            OracleLifecycle = new OracleLifecycleConfiguration { IsolateRetries = true },
            Java            = new JavaConfiguration { HeapSizeMb = 512 },
            WebLogicVersion = WebLogicVersion.WLS_12c,
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    // InstallerFailureClassifier
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Classify_TimedOut_Flag_ReturnsTimeout()
    {
        var cls = InstallerFailureClassifier.Classify(0, "", timedOut: true);
        cls.Should().Be(InstallerFailureClass.Timeout);
    }

    [Fact]
    public void Classify_ExitCodeNeg2_ReturnsTimeout()
    {
        var cls = InstallerFailureClassifier.Classify(-2, "");
        cls.Should().Be(InstallerFailureClass.Timeout);
    }

    [Fact]
    public void Classify_ExitCodeNeg10_ReturnsInventoryConflict()
    {
        var cls = InstallerFailureClassifier.Classify(-10, "");
        cls.Should().Be(InstallerFailureClass.InventoryConflict);
    }

    [Theory]
    [InlineData("INST-07319 encountered")]
    [InlineData("Oracle Home already exists at the specified path")]
    [InlineData("HOME already registered in inventory")]
    [InlineData("already installed at this location")]
    [InlineData("target Oracle Home path is not empty")]
    public void Classify_InventoryConflictSignals(string stderr)
    {
        InstallerFailureClassifier.Classify(1, stderr)
            .Should().Be(InstallerFailureClass.InventoryConflict);
    }

    [Theory]
    [InlineData("orainventory.lock detected")]
    [InlineData("inventory is locked by another process")]
    [InlineData("OUI-10170: Another installation in progress")]
    [InlineData("lock file exists, cannot proceed")]
    public void Classify_LockConflictSignals(string stderr)
    {
        InstallerFailureClassifier.Classify(1, stderr)
            .Should().Be(InstallerFailureClass.LockConflict);
    }

    [Theory]
    [InlineData("java.exe not found in PATH")]
    [InlineData("JVM terminated unexpectedly")]
    [InlineData("Error occurred during initialization of VM")]
    [InlineData("Unable to access jarfile fmw_installer.jar")]
    public void Classify_JavaLaunchSignals(string stderr)
    {
        InstallerFailureClassifier.Classify(1, stderr)
            .Should().Be(InstallerFailureClass.JavaLaunchFailure);
    }

    [Theory]
    [InlineData("response file is missing required parameter")]
    [InlineData("OUI-10182: responseFile argument is malformed")]
    [InlineData("-silent requires a valid RESP_FILE")]
    public void Classify_ResponseFileSignals(string stderr)
    {
        InstallerFailureClassifier.Classify(1, stderr)
            .Should().Be(InstallerFailureClass.ResponseFileIssue);
    }

    [Theory]
    [InlineData("insufficient disk space on target drive")]
    [InlineData("Disk Space check failed")]
    [InlineData("OUI-10133: Prerequisite check failed")]
    [InlineData("kernel parameter shmmax is below minimum")]
    public void Classify_PrerequisiteSignals(string stderr)
    {
        InstallerFailureClassifier.Classify(1, stderr)
            .Should().Be(InstallerFailureClass.PrerequisiteFailure);
    }

    [Theory]
    [InlineData("Cannot create temp file for extraction")]
    [InlineData("java.io.tmpdir is not writable")]
    [InlineData("No space left on device during extracting")]
    [InlineData("Unable to extract installer archive")]
    public void Classify_ExtractionSignals(string stderr)
    {
        InstallerFailureClassifier.Classify(1, stderr)
            .Should().Be(InstallerFailureClass.ExtractionFailure);
    }

    [Theory]
    [InlineData("Rollback in progress, please wait")]
    [InlineData("Install failed after file copy — partial installation detected")]
    public void Classify_PartialInstallSignals(string stderr)
    {
        InstallerFailureClassifier.Classify(1, stderr)
            .Should().Be(InstallerFailureClass.PartialInstall);
    }

    [Fact]
    public void Classify_UnrecognisedError_ReturnsUnknown()
    {
        InstallerFailureClassifier.Classify(1, "Something unexpected happened")
            .Should().Be(InstallerFailureClass.Unknown);
    }

    [Fact]
    public void Classify_InventoryConflict_HasPrecedenceOverLock()
    {
        // Both signals present — InventoryConflict is checked first
        var mixed = "INST-07319 error AND orainventory.lock";
        InstallerFailureClassifier.Classify(1, mixed)
            .Should().Be(InstallerFailureClass.InventoryConflict);
    }

    [Fact]
    public void GetRemediationHint_ReturnsNonEmptyForAllClasses()
    {
        foreach (InstallerFailureClass cls in Enum.GetValues<InstallerFailureClass>())
        {
            InstallerFailureClassifier.GetRemediationHint(cls)
                .Should().NotBeNullOrWhiteSpace(because: $"class {cls} must have a hint");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // InstallerRetryPreflight
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Preflight_CleanEnv_CanProceed()
    {
        var config   = MakeConfig();
        var preflight = new InstallerRetryPreflight(_inventoryMock.Object, _log);

        var result = preflight.Validate(config, "InstallWebLogic", 1);

        result.CanProceed.Should().BeTrue();
        result.BlockingItems.Should().BeEmpty();
        result.Findings.Should().NotBeEmpty();
    }

    [Fact]
    public void Preflight_RegisteredAndPresent_Blocks()
    {
        var config = MakeConfig();
        _inventoryMock
            .Setup(s => s.DetectHomeState(config.Paths.MiddlewareHome, config.Paths.OracleInventory))
            .Returns(OracleHomeState.RegisteredAndPresent);

        var preflight = new InstallerRetryPreflight(_inventoryMock.Object, _log);
        var result    = preflight.Validate(config, "InstallWebLogic", 2);

        result.CanProceed.Should().BeFalse();
        result.BlockingItems.Should().ContainSingle();
        result.BlockingItems[0].Should().Contain("registered AND present");
    }

    [Fact]
    public void Preflight_PartialInstall_Blocks()
    {
        var config = MakeConfig();
        _inventoryMock
            .Setup(s => s.DetectHomeState(config.Paths.MiddlewareHome, config.Paths.OracleInventory))
            .Returns(OracleHomeState.PartialInstall);

        var result = new InstallerRetryPreflight(_inventoryMock.Object, _log)
            .Validate(config, "InstallWebLogic", 2);

        result.CanProceed.Should().BeFalse();
        result.BlockingItems[0].Should().Contain("partial OUI artifacts");
    }

    [Fact]
    public void Preflight_ActiveInventoryLock_Blocks()
    {
        var config = MakeConfig();
        _inventoryMock
            .Setup(s => s.DetectLocks(config.Paths.OracleInventory))
            .Returns(new List<OracleInventoryLockDescriptor>
            {
                new() { LockFilePath = @"C:\Oracle\oraInventory\.lck", IsStale = false },
            }.AsReadOnly());

        var result = new InstallerRetryPreflight(_inventoryMock.Object, _log)
            .Validate(config, "InstallWebLogic", 2);

        result.CanProceed.Should().BeFalse();
        result.BlockingItems.Should().ContainSingle(b => b.Contains("active lock file"));
    }

    [Fact]
    public void Preflight_StaleLockOnly_DoesNotBlock()
    {
        var config = MakeConfig();
        _inventoryMock
            .Setup(s => s.DetectLocks(config.Paths.OracleInventory))
            .Returns(new List<OracleInventoryLockDescriptor>
            {
                new() { LockFilePath = @"C:\Oracle\oraInventory\.lck", IsStale = true },
            }.AsReadOnly());

        var result = new InstallerRetryPreflight(_inventoryMock.Object, _log)
            .Validate(config, "InstallWebLogic", 2);

        result.CanProceed.Should().BeTrue("stale locks are not blocking");
        result.Findings.Should().Contain(f => f.Contains("stale lock"));
    }

    [Fact]
    public void Preflight_NonWritableTempDir_Blocks()
    {
        // Point to a path that can never be created (null bytes aren't valid, so use a
        // path under an existing file, which can't be a directory)
        var bogusParent = Path.Combine(_root, "notadir.txt");
        File.WriteAllText(bogusParent, "I am a file");
        var config = MakeConfig();
        config.Paths.TempDirectory = Path.Combine(bogusParent, "sub");  // can't create under a file

        var result = new InstallerRetryPreflight(_inventoryMock.Object, _log)
            .Validate(config, "InstallWebLogic", 1);

        result.CanProceed.Should().BeFalse();
        result.BlockingItems.Should().ContainSingle(b => b.Contains("not writable"));
    }

    [Fact]
    public void Preflight_PurgesOraInstallCaches_InTempRoot()
    {
        var config  = MakeConfig();
        var cache1  = Path.Combine(config.Paths.TempDirectory, "OraInstall2024-01-01");
        var cache2  = Path.Combine(config.Paths.TempDirectory, "OraInstallABCDEF");
        Directory.CreateDirectory(cache1);
        Directory.CreateDirectory(cache2);

        new InstallerRetryPreflight(_inventoryMock.Object, _log)
            .Validate(config, "InstallWebLogic", 2);

        Directory.Exists(cache1).Should().BeFalse("OraInstall cache 1 should be purged");
        Directory.Exists(cache2).Should().BeFalse("OraInstall cache 2 should be purged");
    }

    [Fact]
    public void Preflight_Findings_AreAlwaysPopulated()
    {
        var config = MakeConfig();
        var result = new InstallerRetryPreflight(_inventoryMock.Object, _log)
            .Validate(config, "InstallWebLogic", 1);

        result.Findings.Should().NotBeEmpty("findings are always populated for observability");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // InstallRetryIsolationService.BuildInstallerContext
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildInstallerContext_CreatesUniqueDirectoriesPerAttempt()
    {
        var config = MakeConfig();

        var ctx1 = _sut.BuildInstallerContext(config, "InstallWebLogic", 1);
        var ctx2 = _sut.BuildInstallerContext(config, "InstallWebLogic", 2);

        ctx1.TempDirectory.Should().NotBe(ctx2.TempDirectory,
            "each attempt must use a distinct temp directory");
        ctx1.ExtractionDirectory.Should().NotBe(ctx2.ExtractionDirectory,
            "each attempt must use a distinct extraction directory");
    }

    [Fact]
    public void BuildInstallerContext_FirstAttempt_UsesTempDirectoryAsBase()
    {
        var config  = MakeConfig();
        var baseDir = config.Paths.TempDirectory;

        var ctx = _sut.BuildInstallerContext(config, "InstallWebLogic", 1);

        ctx.TempDirectory.Should().Be(baseDir,
            "on the first attempt, base temp is used unchanged");
    }

    [Fact]
    public void BuildInstallerContext_RetryAttempt_UsesSubdirectoryOfParentTemp()
    {
        var config = MakeConfig();
        var parent = Path.GetDirectoryName(config.Paths.TempDirectory)!;

        var ctx = _sut.BuildInstallerContext(config, "InstallWebLogic", 2);

        ctx.TempDirectory.Should().StartWith(parent,
            "retry attempt temp dir must be a sibling of the base temp");
        ctx.TempDirectory.Should().Contain("wedm-retry-",
            "retry attempt dir must have the 'wedm-retry-' prefix");
    }

    [Fact]
    public void BuildInstallerContext_CreatesAllDirectoriesOnDisk()
    {
        var config = MakeConfig();

        var ctx = _sut.BuildInstallerContext(config, "InstallWebLogic", 2);

        Directory.Exists(ctx.TempDirectory).Should().BeTrue("temp dir must be created");
        Directory.Exists(ctx.ExtractionDirectory).Should().BeTrue("extraction dir must be created");
        Directory.Exists(ctx.OuiLogDirectory).Should().BeTrue("OUI log dir must be created");
    }

    [Fact]
    public void BuildInstallerContext_ExtractionDir_IsChildOfTemp()
    {
        var config = MakeConfig();
        var ctx    = _sut.BuildInstallerContext(config, "InstallWebLogic", 2);

        ctx.ExtractionDirectory.Should().StartWith(ctx.TempDirectory,
            "extraction dir must be under the attempt temp dir");
    }

    [Fact]
    public void BuildInstallerContext_GeneratesResponseFileInIsolatedTemp()
    {
        var config = MakeConfig();
        var ctx    = _sut.BuildInstallerContext(config, "InstallWebLogic", 1);

        ctx.ResponseFilePath.Should().NotBeNullOrEmpty();
        File.Exists(ctx.ResponseFilePath).Should().BeTrue("response file must be written to disk");
        ctx.ResponseFilePath.Should().StartWith(ctx.TempDirectory,
            "response file must be located inside the attempt's temp dir");
    }

    [Fact]
    public void BuildInstallerContext_WritesOraInstLoc()
    {
        var config = MakeConfig();
        Directory.CreateDirectory(config.Paths.OracleInventory);
        var ctx = _sut.BuildInstallerContext(config, "InstallWebLogic", 1);

        ctx.InventoryPointerPath.Should().EndWith("oraInst.loc");
        File.Exists(ctx.InventoryPointerPath).Should().BeTrue();
        File.ReadAllText(ctx.InventoryPointerPath)
            .Should().Contain(config.Paths.OracleInventory);
    }

    [Fact]
    public void BuildInstallerContext_SetsCurrentInstallerContextOnConfig()
    {
        var config = MakeConfig();
        var ctx    = _sut.BuildInstallerContext(config, "InstallWebLogic", 1);

        config.CurrentInstallerContext.Should().NotBeNull();
        config.CurrentInstallerContext!.TempDirectory.Should().Be(ctx.TempDirectory);
    }

    [Fact]
    public void BuildInstallerContext_StoresAttemptNumber()
    {
        var config = MakeConfig();
        var ctx    = _sut.BuildInstallerContext(config, "InstallWebLogic", 3);

        ctx.AttemptNumber.Should().Be(3);
    }

    [Fact]
    public void BuildInstallerContext_StoresPreviousFailureClass()
    {
        var config = MakeConfig();
        var ctx    = _sut.BuildInstallerContext(
            config, "InstallWebLogic", 2, InstallerFailureClass.Timeout);

        ctx.PreviousFailureClass.Should().Be(InstallerFailureClass.Timeout);
    }

    [Fact]
    public void BuildInstallerContext_RegistersCleanupPaths()
    {
        var config = MakeConfig();
        var ctx    = _sut.BuildInstallerContext(config, "InstallWebLogic", 2);

        ctx.CleanupPaths.Should().NotBeEmpty("retry attempts must register cleanup paths");
        ctx.CleanupPaths.Should().Contain(ctx.ExtractionDirectory);
        ctx.CleanupPaths.Should().Contain(ctx.OuiLogDirectory);
        // On attempt > 1 the whole attempt dir is also registered
        ctx.CleanupPaths.Should().Contain(ctx.TempDirectory);
    }

    [Fact]
    public void BuildInstallerContext_FirstAttempt_DoesNotRegisterTempInCleanup()
    {
        var config = MakeConfig();
        var ctx    = _sut.BuildInstallerContext(config, "InstallWebLogic", 1);

        // On attempt 1, base temp == TempDirectory — we should NOT purge it
        ctx.CleanupPaths.Should().NotContain(ctx.TempDirectory,
            "first attempt base temp is shared and must not be scheduled for deletion");
    }

    [Fact]
    public void BuildInstallerContext_StepNameDispatch_FormsUsesFormsResponseFile()
    {
        var config = MakeConfig();
        var ctx    = _sut.BuildInstallerContext(config, "InstallFormsReports", 1);

        ctx.ResponseFilePath.Should().Contain("forms_install",
            "Forms step must generate forms response file");
    }

    [Fact]
    public void BuildInstallerContext_StepNameDispatch_OhsUsesOhsResponseFile()
    {
        var config = MakeConfig();
        var ctx    = _sut.BuildInstallerContext(config, "InstallOHSWebTier", 1);

        ctx.ResponseFilePath.Should().Contain("ohs_install",
            "OHS step must generate OHS response file");
    }

    [Fact]
    public void BuildInstallerContext_StepNameDispatch_UnknownDefaultsToWebLogic()
    {
        var config = MakeConfig();
        var ctx    = _sut.BuildInstallerContext(config, "SomeUnrecognisedStep", 1);

        ctx.ResponseFilePath.Should().Contain("wls_install",
            "unrecognised step must fall back to WebLogic response file");
    }

    [Fact]
    public void BuildInstallerContext_Wls11g_GeneratesSilentXml()
    {
        var config = MakeConfig();
        config.WebLogicVersion = WebLogicVersion.WLS_11g;

        var ctx = _sut.BuildInstallerContext(config, "InstallWebLogic", 1);

        ctx.SilentXmlPath.Should().NotBeNullOrEmpty(
            "WLS 11g installs require a silent_xml path");
        File.Exists(ctx.SilentXmlPath).Should().BeTrue();
    }

    [Fact]
    public void BuildInstallerContext_Wls12c_SilentXmlIsNull()
    {
        var config = MakeConfig();
        config.WebLogicVersion = WebLogicVersion.WLS_12c;

        var ctx = _sut.BuildInstallerContext(config, "InstallWebLogic", 1);

        ctx.SilentXmlPath.Should().BeNull("WLS 12c+ uses response file, not silent_xml");
    }

    [Fact]
    public void BuildInstallerContext_RestoresTempDirectoryAfterGeneration()
    {
        var config          = MakeConfig();
        var originalTempDir = config.Paths.TempDirectory;

        _sut.BuildInstallerContext(config, "InstallWebLogic", 1);

        config.Paths.TempDirectory.Should().Be(originalTempDir,
            "BuildInstallerContext must restore the original TempDirectory after response file generation");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // InstallRetryIsolationService.PrepareRetryAttempt
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PrepareRetryAttempt_FirstAttempt_ReturnsBaseTemp()
    {
        var config   = MakeConfig();
        var baseTemp = config.Paths.TempDirectory;

        var result = _sut.PrepareRetryAttempt(config, "InstallWebLogic", 1);

        result.IsolatedTempDirectory.Should().Be(baseTemp,
            "first attempt is not isolated — base temp is returned");
    }

    [Fact]
    public void PrepareRetryAttempt_IsolationDisabled_ReturnsBaseTempOnRetry()
    {
        var config = MakeConfig();
        config.OracleLifecycle.IsolateRetries = false;
        var baseTemp = config.Paths.TempDirectory;

        var result = _sut.PrepareRetryAttempt(config, "InstallWebLogic", 2);

        result.IsolatedTempDirectory.Should().Be(baseTemp,
            "isolation disabled — base temp used regardless of attempt number");
    }

    [Fact]
    public void PrepareRetryAttempt_SecondAttempt_IsolatesTemp()
    {
        var config   = MakeConfig();
        var baseTemp = config.Paths.TempDirectory;

        var result = _sut.PrepareRetryAttempt(config, "InstallWebLogic", 2);

        result.IsolatedTempDirectory.Should().NotBe(baseTemp,
            "retry attempt must use a unique isolated temp directory");
        config.Paths.TempDirectory.Should().Be(result.IsolatedTempDirectory,
            "PrepareRetryAttempt must mutate config.Paths.TempDirectory for backward compatibility");
    }

    [Fact]
    public void PrepareRetryAttempt_SetsCurrentInstallerContext()
    {
        var config = MakeConfig();
        _sut.PrepareRetryAttempt(config, "InstallWebLogic", 2);

        config.CurrentInstallerContext.Should().NotBeNull(
            "PrepareRetryAttempt must set CurrentInstallerContext on retry");
        config.CurrentInstallerContext!.AttemptNumber.Should().Be(2);
    }

    [Fact]
    public void PrepareRetryAttempt_PropagatesPreviousFailureClass()
    {
        var config = MakeConfig();

        // Simulate previous attempt having recorded a Timeout failure
        config.CurrentInstallerContext = new InstallerExecutionContext
        {
            AttemptNumber        = 1,
            PreviousFailureClass = InstallerFailureClass.Timeout,
        };

        _sut.PrepareRetryAttempt(config, "InstallWebLogic", 2);

        // The new context should carry the failure class from the previous attempt
        config.CurrentInstallerContext!.PreviousFailureClass
            .Should().Be(InstallerFailureClass.Timeout,
            "the classified failure from the previous attempt must propagate into the new context");
    }

    [Fact]
    public void PrepareRetryAttempt_ActionsContainIsolationSummary()
    {
        var config = MakeConfig();
        var result = _sut.PrepareRetryAttempt(config, "InstallWebLogic", 2);

        result.Actions.Should().NotBeEmpty();
        result.Actions.Should().Contain(a => a.Contains("Isolated temp"));
    }

    [Fact]
    public void PrepareRetryAttempt_RegeneratesResponseFile()
    {
        var config = MakeConfig();
        var result = _sut.PrepareRetryAttempt(config, "InstallWebLogic", 2);

        result.RegeneratedResponseFile.Should().NotBeNullOrEmpty(
            "a fresh response file must be generated in the isolated temp");
        File.Exists(result.RegeneratedResponseFile).Should().BeTrue(
            "regenerated response file must be written to disk");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // InstallRetryIsolationService.RunPreflight
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RunPreflight_CleanEnv_CanProceed()
    {
        var config = MakeConfig();
        _sut.BuildInstallerContext(config, "InstallWebLogic", 1);

        var result = _sut.RunPreflight(config, "InstallWebLogic", 1);

        result.CanProceed.Should().BeTrue();
    }

    [Fact]
    public void RunPreflight_BlockingInventory_CannotProceed()
    {
        var config = MakeConfig();
        _inventoryMock
            .Setup(s => s.DetectHomeState(config.Paths.MiddlewareHome, config.Paths.OracleInventory))
            .Returns(OracleHomeState.RegisteredAndPresent);

        var result = _sut.RunPreflight(config, "InstallWebLogic", 2);

        result.CanProceed.Should().BeFalse();
        result.BlockingItems.Should().NotBeEmpty();
    }

    [Fact]
    public void RunPreflight_LogsResults()
    {
        var config = MakeConfig();
        _sut.RunPreflight(config, "InstallWebLogic", 1);

        var entries = _log.GetEntries();
        entries.Should().Contain(e => e.Message.Contains("InstallerPreflight"),
            "preflight results should be logged under 'InstallerPreflight' category");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Context isolation across multiple retries
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MultipleRetries_EachAttemptHasUniqueExtractionDir()
    {
        var config = MakeConfig();
        var dirs   = new HashSet<string>();

        for (int attempt = 2; attempt <= 4; attempt++)
        {
            var result = _sut.PrepareRetryAttempt(config, "InstallWebLogic", attempt);
            dirs.Add(config.CurrentInstallerContext!.ExtractionDirectory);
        }

        dirs.Count.Should().Be(3,
            "each of the 3 retry attempts must have a distinct extraction directory");
    }

    [Fact]
    public void MultipleRetries_CleanupPathsEscalate()
    {
        var config = MakeConfig();

        // Attempt 1 — base temp, not in cleanup
        var ctx1 = _sut.BuildInstallerContext(config, "InstallWebLogic", 1);
        ctx1.CleanupPaths.Should().NotContain(ctx1.TempDirectory);

        // Attempt 2 — whole attempt dir IS in cleanup
        var ctx2 = _sut.BuildInstallerContext(config, "InstallWebLogic", 2);
        ctx2.CleanupPaths.Should().Contain(ctx2.TempDirectory);

        // Attempt 3 — same pattern
        var ctx3 = _sut.BuildInstallerContext(config, "InstallWebLogic", 3);
        ctx3.CleanupPaths.Should().Contain(ctx3.TempDirectory);
    }

    [Fact]
    public void UniqueIds_AreDistinctAcrossAttempts()
    {
        var config = MakeConfig();
        var ids    = new HashSet<Guid>();

        for (int i = 1; i <= 5; i++)
            ids.Add(_sut.BuildInstallerContext(config, "InstallWebLogic", i).UniqueId);

        ids.Count.Should().Be(5, "every attempt must have a unique GUID");
    }
}
