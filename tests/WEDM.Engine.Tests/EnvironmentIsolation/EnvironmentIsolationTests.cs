using FluentAssertions;
using Moq;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.EnvironmentIsolation;

namespace WEDM.Engine.Tests.EnvironmentIsolation;

// ═══════════════════════════════════════════════════════════════════════════════
// PathSanitizer tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PathSanitizerTests
{
    // ── Split ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_EmptyString_ReturnsEmpty()
    {
        PathSanitizer.Split("").Should().BeEmpty();
        PathSanitizer.Split(null).Should().BeEmpty();
    }

    [Fact]
    public void Split_SemicolonDelimited_ReturnsTrimmedSegments()
    {
        var result = PathSanitizer.Split(@"C:\Windows\System32;C:\Windows;");
        result.Should().HaveCount(2);
        result[0].Should().Be(@"C:\Windows\System32");
    }

    // ── NormalizePath ─────────────────────────────────────────────────────────

    [Fact]
    public void NormalizePath_ForwardSlashes_ConvertedToBackslash()
    {
        PathSanitizer.NormalizePath("C:/Oracle/Middleware/").Should().Be(@"C:\Oracle\Middleware");
    }

    [Fact]
    public void NormalizePath_TrailingSeparatorStripped()
    {
        PathSanitizer.NormalizePath(@"C:\Windows\System32\").Should().Be(@"C:\Windows\System32");
    }

    // ── IsStaleOraclePath ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Oracle\Middleware\oracle_common\bin")]
    [InlineData(@"C:\Oracle\Middleware\OPatch")]
    [InlineData(@"C:\Program Files\Java\jdk1.8.0_202\bin")]
    [InlineData(@"C:\Program Files\Java\jdk-21\bin")]
    [InlineData(@"C:\Oracle\wlserver\server\bin")]
    public void IsStaleOraclePath_KnownOracleJdkPaths_ReturnsTrue(string path)
    {
        PathSanitizer.IsStaleOraclePath(PathSanitizer.NormalizePath(path)).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0")]
    [InlineData(@"C:\Program Files\Git\bin")]
    public void IsStaleOraclePath_NonOraclePaths_ReturnsFalse(string path)
    {
        PathSanitizer.IsStaleOraclePath(PathSanitizer.NormalizePath(path)).Should().BeFalse();
    }

    // ── IsWindowsSystemPath ───────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\SysWOW64")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0")]
    [InlineData(@"C:\Windows\System32\wbem")]
    public void IsWindowsSystemPath_SystemPaths_ReturnsTrue(string path)
    {
        PathSanitizer.IsWindowsSystemPath(PathSanitizer.NormalizePath(path)).Should().BeTrue();
    }

    // ── Build: stale Oracle entry exclusion ───────────────────────────────────

    [Fact]
    public void Build_StaleOraclePathInMachinePath_Excluded()
    {
        var machine = @"C:\Oracle\Middleware\oracle_common\bin;C:\Windows\System32;C:\Program Files\Java\jdk1.8.0_202\bin";
        var result  = PathSanitizer.Build(machine);
        result.Should().NotContain("oracle_common");
        result.Should().NotContain("jdk1.8");
        result.Should().Contain(@"C:\Windows\System32");
    }

    [Fact]
    public void Build_PrependPaths_AppearedFirst()
    {
        var machine = @"C:\Windows\System32";
        var prepend = new[] { @"C:\Java\jdk17\bin" };
        var result  = PathSanitizer.Build(machine, prepend);
        result.Should().StartWith(@"C:\Java\jdk17\bin");
    }

    [Fact]
    public void Build_DuplicateSegments_Deduplicated()
    {
        var machine = @"C:\Windows\System32;C:\Windows\System32;C:\Windows\System32";
        var result  = PathSanitizer.Build(machine);
        result.Split(';').Where(s => s == @"C:\Windows\System32").Should().HaveCount(1);
    }

    [Fact]
    public void Build_ForwardSlashMachinePath_NormalisedToBackslash()
    {
        var machine = "C:/Windows/System32;C:/Windows";
        var result  = PathSanitizer.Build(machine);
        result.Should().Contain(@"C:\Windows\System32");
        result.Should().NotContain("C:/");
    }

    [Fact]
    public void Build_MaxIsolation_OnlySystemPaths()
    {
        var machine = @"C:\Windows\System32;C:\Program Files\Git\bin;C:\Oracle\Middleware\OPatch";
        var result  = PathSanitizer.Build(machine, includeNonOracle: false);
        result.Should().Contain(@"C:\Windows\System32");
        result.Should().NotContain("Git");
        result.Should().NotContain("OPatch");
    }

    // ── Analyse ───────────────────────────────────────────────────────────────

    [Fact]
    public void Analyse_ClassifiesSegmentsCorrectly()
    {
        var path = @"C:\Windows\System32;C:\Oracle\Middleware\OPatch;C:\Program Files\Java\jdk1.8.0_202\bin;C:\Program Files\Git\bin";
        var r    = PathSanitizer.Analyse(path);

        r.SystemSegments.Should().NotBeEmpty();
        r.StaleSegments.Should().HaveCountGreaterThan(0);  // OPatch + JDK
        r.HasStaleEntries.Should().BeTrue();
    }

    [Fact]
    public void Analyse_DuplicatesDetected()
    {
        var path = @"C:\Windows\System32;C:\Windows\System32;C:\foo";
        var r    = PathSanitizer.Analyse(path);
        r.HasDuplicates.Should().BeTrue();
        r.DuplicateSegments.Should().Contain(@"C:\Windows\System32");
    }

    [Fact]
    public void Analyse_MissingRequired_Reported()
    {
        var path     = @"C:\Windows\System32";
        var required = new[] { @"C:\Java\jdk17\bin" };
        var r        = PathSanitizer.Analyse(path, required);
        r.HasMissingRequired.Should().BeTrue();
        r.MissingRequired.Should().Contain(@"C:\Java\jdk17\bin");
    }

    // ── Diff ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Diff_AddedAndRemoved_Reported()
    {
        var baseline = @"C:\Windows\System32;C:\OldOracle\bin";
        var current  = @"C:\Windows\System32;C:\NewJava\bin";
        var diff     = PathSanitizer.Diff(baseline, current);

        diff.Should().Contain(s => s.StartsWith("(+)") && s.Contains("NewJava"));
        diff.Should().Contain(s => s.StartsWith("(-)") && s.Contains("OldOracle"));
    }

    [Fact]
    public void Diff_NoChange_EmptyResult()
    {
        var path = @"C:\Windows\System32";
        PathSanitizer.Diff(path, path).Should().BeEmpty();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ProcessEnvironmentBuilder tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ProcessEnvironmentBuilderTests
{
    private static DeploymentEnvironmentContext MakeCtx(string javaHome = @"C:\Java\jdk17", string mwHome = @"C:\Oracle\MW") => new()
    {
        JavaHome        = javaHome,
        MiddlewareHome  = mwHome,
        OracleHome      = mwHome,
        TempRoot        = @"C:\Temp\wedm-session-abc12345",
        SanitizedPath   = @"C:\Java\jdk17\bin;C:\Windows\System32",
        ClearClasspath  = true,
        ClearStaleOracleVars = true,
        ClearWlstResiduals   = true,
        ClearJvmOverrideVars = true,
        ClearOpatchResiduals = true,
    };

    private readonly ProcessEnvironmentBuilder _builder = new();

    // ── Universal clear vars ──────────────────────────────────────────────────

    [Theory]
    [InlineData(OracleTool.OUI)]
    [InlineData(OracleTool.WLST)]
    [InlineData(OracleTool.OPatch)]
    [InlineData(OracleTool.NodeManager)]
    public void Build_AllTools_ClearClasspath(OracleTool tool)
    {
        var env = _builder.Build(tool, MakeCtx());
        env.ClearVariables.Should().Contain("CLASSPATH");
    }

    [Theory]
    [InlineData(OracleTool.OUI)]
    [InlineData(OracleTool.WLST)]
    [InlineData(OracleTool.OPatch)]
    public void Build_AllTools_ClearJvmOverrideVars(OracleTool tool)
    {
        var env = _builder.Build(tool, MakeCtx());
        env.ClearVariables.Should().Contain("JAVA_OPTS");
        env.ClearVariables.Should().Contain("_JAVA_OPTIONS");
    }

    // ── OUI specifics ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_OUI_SetsJavaHomeAndTemp()
    {
        var env = _builder.Build(OracleTool.OUI, MakeCtx());
        env.SetVariables.Should().ContainKey("JAVA_HOME").WhoseValue.Should().Be(@"C:\Java\jdk17");
        env.SetVariables.Should().ContainKey("TEMP");
        env.SetVariables.Should().ContainKey("TMP");
    }

    [Fact]
    public void Build_OUI_DoesNotSetOracleHome()
    {
        // OUI must discover ORACLE_HOME itself; WEDM should not pre-set it
        var env = _builder.Build(OracleTool.OUI, MakeCtx());
        env.SetVariables.Should().NotContainKey("ORACLE_HOME");
    }

    [Fact]
    public void Build_OUI_ClearsStaleOracleVars()
    {
        var env = _builder.Build(OracleTool.OUI, MakeCtx());
        env.ClearVariables.Should().Contain("ORACLE_HOME");
        env.ClearVariables.Should().Contain("WL_HOME");
        env.ClearVariables.Should().Contain("MW_HOME");
    }

    // ── WLST specifics ────────────────────────────────────────────────────────

    [Fact]
    public void Build_WLST_SetsOracleHomeAndJavaHome()
    {
        var env = _builder.Build(OracleTool.WLST, MakeCtx());
        env.SetVariables.Should().ContainKey("ORACLE_HOME").WhoseValue.Should().Be(@"C:\Oracle\MW");
        env.SetVariables.Should().ContainKey("JAVA_HOME").WhoseValue.Should().Be(@"C:\Java\jdk17");
    }

    [Fact]
    public void Build_WLST_ClearsWlstResiduals()
    {
        var env = _builder.Build(OracleTool.WLST, MakeCtx());
        env.ClearVariables.Should().Contain("WLST_HOME");
        env.ClearVariables.Should().Contain("WLST_PROPERTIES");
    }

    // ── OPatch specifics ──────────────────────────────────────────────────────

    [Fact]
    public void Build_OPatch_SetsOracleHomeAndPath()
    {
        var env = _builder.Build(OracleTool.OPatch, MakeCtx());
        env.SetVariables.Should().ContainKey("ORACLE_HOME");
        env.SetVariables["PATH"].Should().Contain("OPatch");
    }

    [Fact]
    public void Build_OPatch_ClearsOpatchDebug()
    {
        var env = _builder.Build(OracleTool.OPatch, MakeCtx());
        env.ClearVariables.Should().Contain("OPATCH_DEBUG");
    }

    // ── NodeManager specifics ─────────────────────────────────────────────────

    [Fact]
    public void Build_NodeManager_SetsWlHome()
    {
        var env = _builder.Build(OracleTool.NodeManager, MakeCtx());
        env.SetVariables.Should().ContainKey("WL_HOME");
        env.SetVariables["WL_HOME"].Should().EndWith("wlserver");
    }

    // ── PowerShell preamble ───────────────────────────────────────────────────

    [Fact]
    public void Build_OUI_PreambleContainsRemoveItemForClearedVars()
    {
        var env = _builder.Build(OracleTool.OUI, MakeCtx());
        env.PowerShellPreamble.Should().Contain("Remove-Item Env:CLASSPATH");
        env.PowerShellPreamble.Should().Contain("Remove-Item Env:JAVA_OPTS");
    }

    [Fact]
    public void Build_WLST_PreambleContainsSetOracle()
    {
        var env = _builder.Build(OracleTool.WLST, MakeCtx());
        env.PowerShellPreamble.Should().Contain("$env:ORACLE_HOME");
        env.PowerShellPreamble.Should().Contain("$env:JAVA_HOME");
    }

    [Fact]
    public void Build_PreambleSingleQuotesEscaped()
    {
        // Paths containing single quotes must be safely escaped for PowerShell
        var ctx = MakeCtx(javaHome: @"C:\Java's jdk");
        var env = _builder.Build(OracleTool.OUI, ctx);
        env.PowerShellPreamble.Should().Contain("C:\\Java''s jdk");
    }

    // ── SetVariables and ClearVariables don't overlap ─────────────────────────

    [Theory]
    [InlineData(OracleTool.OUI)]
    [InlineData(OracleTool.WLST)]
    [InlineData(OracleTool.OPatch)]
    [InlineData(OracleTool.NodeManager)]
    [InlineData(OracleTool.Generic)]
    public void Build_SetAndClearLists_DoNotOverlap(OracleTool tool)
    {
        var env = _builder.Build(tool, MakeCtx());
        var overlap = env.SetVariables.Keys.Intersect(env.ClearVariables, StringComparer.OrdinalIgnoreCase);
        overlap.Should().BeEmpty($"variable cannot be both set and cleared for {tool}");
    }

    // ── Temp isolation ────────────────────────────────────────────────────────

    [Fact]
    public void Build_OUI_TempScopedToSessionTempRoot()
    {
        var ctx = MakeCtx();
        var env = _builder.Build(OracleTool.OUI, ctx);
        env.SetVariables["TEMP"].Should().Be(ctx.TempRoot);
        env.SetVariables["TMP"].Should().Be(ctx.TempRoot);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// EnvironmentDriftDetector tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class EnvironmentDriftDetectorTests
{
    private static EnvironmentSnapshot MakeSnapshot(
        SnapshotKind kind = SnapshotKind.PreDeployment,
        string? oracleHome = null,
        string? javaHome   = null,
        string? path       = null)
        => new()
        {
            Kind       = kind,
            SessionId  = Guid.NewGuid(),
            OracleHome = oracleHome,
            JavaHome   = javaHome,
            Path       = path,
            PathSegments = PathSanitizer.Split(path),
        };

    [Fact]
    public void Detect_NoChange_NoDrift()
    {
        var snap = MakeSnapshot(path: @"C:\Windows\System32");
        var report = EnvironmentDriftDetector.Detect(snap, snap);
        report.HasDrift.Should().BeFalse();
    }

    [Fact]
    public void Detect_ScalarAdded_Finding_Added()
    {
        var before = MakeSnapshot();
        var after  = MakeSnapshot(oracleHome: @"C:\Oracle\MW");
        var report = EnvironmentDriftDetector.Detect(before, after);
        report.HasDrift.Should().BeTrue();
        report.Findings.Should().Contain(f => f.Kind == DriftKind.Added && f.VariableName == "ORACLE_HOME");
    }

    [Fact]
    public void Detect_ScalarRemoved_Finding_Removed()
    {
        var before = MakeSnapshot(javaHome: @"C:\Java\old");
        var after  = MakeSnapshot(javaHome: null);
        var report = EnvironmentDriftDetector.Detect(before, after);
        report.Findings.Should().Contain(f => f.Kind == DriftKind.Removed && f.VariableName == "JAVA_HOME");
    }

    [Fact]
    public void Detect_ScalarChanged_Finding_Changed()
    {
        var before = MakeSnapshot(javaHome: @"C:\Java\jdk8");
        var after  = MakeSnapshot(javaHome: @"C:\Java\jdk17");
        var report = EnvironmentDriftDetector.Detect(before, after);
        report.Findings.Should().Contain(f => f.Kind == DriftKind.Changed && f.VariableName == "JAVA_HOME");
    }

    [Fact]
    public void Detect_PathSegmentAdded_Finding_PathAdded()
    {
        var before = MakeSnapshot(path: @"C:\Windows\System32");
        var after  = MakeSnapshot(path: @"C:\Windows\System32;C:\Oracle\Middleware\OPatch");
        var report = EnvironmentDriftDetector.Detect(before, after);
        report.Findings.Should().Contain(f => f.Kind == DriftKind.PathAdded && f.CurrentValue!.Contains("OPatch"));
    }

    [Fact]
    public void Detect_PathSegmentRemoved_Finding_PathRemoved()
    {
        var before = MakeSnapshot(path: @"C:\Windows\System32;C:\SomeOldPath");
        var after  = MakeSnapshot(path: @"C:\Windows\System32");
        var report = EnvironmentDriftDetector.Detect(before, after);
        report.Findings.Should().Contain(f => f.Kind == DriftKind.PathRemoved);
    }

    [Fact]
    public void Detect_ExpectedMutation_TaggedAsExpected()
    {
        var before   = MakeSnapshot();
        var after    = MakeSnapshot(oracleHome: @"C:\Oracle");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ORACLE_HOME" };

        var report = EnvironmentDriftDetector.Detect(before, after, expected);
        report.Findings.Should().AllSatisfy(f => f.IsExpected.Should().BeTrue());
        report.UnexpectedFindings.Should().BeEmpty();
    }

    [Fact]
    public void Detect_UnexpectedMutation_TaggedAsUnexpected()
    {
        var before = MakeSnapshot();
        var after  = MakeSnapshot(oracleHome: @"C:\Oracle\Unexpected");
        var report = EnvironmentDriftDetector.Detect(before, after, expectedMutations: null);
        report.UnexpectedFindings.Should().NotBeEmpty();
    }

    [Fact]
    public void Detect_SamePathDifferentOrder_PathReordered()
    {
        var before = MakeSnapshot(path: @"C:\A;C:\B");
        var after  = MakeSnapshot(path: @"C:\B;C:\A");
        var report = EnvironmentDriftDetector.Detect(before, after);
        report.Findings.Should().Contain(f => f.Kind == DriftKind.PathReordered);
    }

    [Fact]
    public void BuildExpectedMutationSet_AggregatesAllInjections()
    {
        var builder = new ProcessEnvironmentBuilder();
        var ctx     = new DeploymentEnvironmentContext
        {
            JavaHome       = @"C:\Java\jdk17",
            MiddlewareHome = @"C:\Oracle\MW",
            OracleHome     = @"C:\Oracle\MW",
            TempRoot       = @"C:\Temp\test",
        };

        var ouiEnv  = builder.Build(OracleTool.OUI, ctx);
        var wlstEnv = builder.Build(OracleTool.WLST, ctx);

        var set = EnvironmentDriftDetector.BuildExpectedMutationSet([ouiEnv, wlstEnv]);
        set.Should().Contain("JAVA_HOME");
        set.Should().Contain("CLASSPATH");   // in clear list
        set.Should().Contain("ORACLE_HOME"); // set by WLST
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// EnvironmentIsolationService tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class EnvironmentIsolationServiceTests
{
    private static ILoggingService MockLog()
    {
        var m = new Mock<ILoggingService>();
        return m.Object;
    }

    private static DeploymentConfiguration MakeConfig(
        string javaHome  = @"C:\Java\jdk17",
        string mwHome    = @"C:\Oracle\MW",
        string tempDir   = @"C:\Temp",
        string inventory = @"C:\Oracle\Inventory")
    {
        var c = new DeploymentConfiguration();
        c.Java.JavaHome = javaHome;
        c.Paths.MiddlewareHome = mwHome;
        c.Paths.TempDirectory  = tempDir;
        c.Paths.OracleInventory = inventory;
        return c;
    }

    // ── CaptureSnapshot ───────────────────────────────────────────────────────

    [Fact]
    public void CaptureSnapshot_DoesNotThrow()
    {
        var svc = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        snap.Should().NotBeNull();
        snap.Kind.Should().Be(SnapshotKind.PreDeployment);
    }

    [Fact]
    public void CaptureSnapshot_PathSegmentsParsed()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        snap.PathSegments.Should().NotBeNull();
        // PATH should be non-empty on any Windows CI machine
        // (If PATH is absent in the test environment, PathSegments will simply be empty — still valid)
    }

    // ── BuildContext ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildContext_SessionIdAssigned()
    {
        var svc   = new EnvironmentIsolationService(MockLog());
        var snap  = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx   = svc.BuildContext(MakeConfig(), snap);
        ctx.SessionId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void BuildContext_JavaHomeTakenFromConfig()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(javaHome: @"C:\Java\jdk21"), snap);
        ctx.JavaHome.Should().Be(@"C:\Java\jdk21");
    }

    [Fact]
    public void BuildContext_TempRootContainsWedmSession()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(), snap);
        ctx.TempRoot.Should().Contain("wedm-session");
    }

    [Fact]
    public void BuildContext_SanitizedPathDoesNotContainStaleOracle()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(), snap);
        // If machine PATH happens to have Oracle entries, they must be removed
        if (ctx.SanitizedPath.Length > 0)
            PathSanitizer.Analyse(ctx.SanitizedPath).StaleSegments.Should().BeEmpty();
    }

    [Fact]
    public void BuildContext_IsolationFlagsDefaultTrue()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(), snap);
        ctx.ClearClasspath.Should().BeTrue();
        ctx.ClearStaleOracleVars.Should().BeTrue();
        ctx.ClearWlstResiduals.Should().BeTrue();
        ctx.ClearJvmOverrideVars.Should().BeTrue();
        ctx.ClearOpatchResiduals.Should().BeTrue();
    }

    // ── BuildIsolatedEnvironment ──────────────────────────────────────────────

    [Fact]
    public void BuildIsolatedEnvironment_OUI_HasPreamble()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(), snap);

        var env = svc.BuildIsolatedEnvironment(OracleTool.OUI, ctx);
        env.PowerShellPreamble.Should().NotBeNullOrWhiteSpace();
        env.PowerShellPreamble.Should().Contain("Remove-Item Env:");
    }

    [Fact]
    public void BuildIsolatedEnvironment_TracksInjectionForDrift()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(), snap);

        svc.BuildIsolatedEnvironment(OracleTool.OUI, ctx);
        svc.BuildIsolatedEnvironment(OracleTool.WLST, ctx);

        // Post-snapshot with changed ORACLE_HOME — WEDM expected it, so should be marked expected
        var after = new EnvironmentSnapshot
        {
            Kind       = SnapshotKind.PostDeployment,
            SessionId  = ctx.SessionId,
            OracleHome = @"C:\Oracle\MW",
        };

        var drift = svc.DetectDrift(snap, after, ctx);
        // If ORACLE_HOME changed: because WLST injects it, it should be expected
        var oracleFinding = drift.Findings.FirstOrDefault(f => f.VariableName == "ORACLE_HOME");
        if (oracleFinding is not null)
            oracleFinding.IsExpected.Should().BeTrue();
    }

    // ── ValidateBeforeLaunch ──────────────────────────────────────────────────

    [Fact]
    public void ValidateBeforeLaunch_MissingJavaHome_BlocksLaunch()
    {
        var svc = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(javaHome: ""), snap);
        var env  = svc.BuildIsolatedEnvironment(OracleTool.WLST, ctx);

        var result = svc.ValidateBeforeLaunch(OracleTool.WLST, env, ctx);
        result.IsValid.Should().BeFalse();
        result.HasBlockers.Should().BeTrue();
    }

    [Fact]
    public void ValidateBeforeLaunch_JdkInstaller_NoJavaHomeRequired()
    {
        var svc = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        // JdkInstaller: JavaHome is empty because we're INSTALLING Java
        var ctx  = svc.BuildContext(MakeConfig(javaHome: ""), snap);
        var env  = svc.BuildIsolatedEnvironment(OracleTool.JdkInstaller, ctx);

        var result = svc.ValidateBeforeLaunch(OracleTool.JdkInstaller, env, ctx);
        // JdkInstaller skips the JavaHome check — should not block on missing JavaHome
        result.Blockers.Should().NotContain(b => b.Contains("JAVA_HOME"));
    }

    [Fact]
    public void ValidateBeforeLaunch_ClasspathCleared_Reported()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(), snap);
        var env  = svc.BuildIsolatedEnvironment(OracleTool.Generic, ctx);

        var result = svc.ValidateBeforeLaunch(OracleTool.Generic, env, ctx);
        result.Findings.Should().Contain(f => f.Contains("CLASSPATH"));
    }

    // ── ApplyScopedVariables (process-level scope only) ───────────────────────

    [Fact]
    public void ApplyScopedVariables_SetsAndRestoresOnDispose()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(), snap);

        // Use a unique variable name to avoid collision with real machine vars
        const string testVarName  = "WEDM_TEST_ISO_VAR_12345";
        const string testVarValue = "isolation-test-value";

        var env = new IsolatedEnvironmentVariables
        {
            Tool         = OracleTool.Generic,
            SetVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { [testVarName] = testVarValue },
            ClearVariables   = [],
            PowerShellPreamble = string.Empty,
        };

        Environment.GetEnvironmentVariable(testVarName).Should().BeNull("test var should not exist yet");

        using (svc.ApplyScopedVariables(env))
        {
            Environment.GetEnvironmentVariable(testVarName).Should().Be(testVarValue);
        }

        Environment.GetEnvironmentVariable(testVarName).Should().BeNull("variable should be restored to null");
    }

    [Fact]
    public void ApplyScopedVariables_ClearsAndRestoresOnDispose()
    {
        var svc = new EnvironmentIsolationService(MockLog());

        const string testVarName  = "WEDM_TEST_ISO_CLEAR_VAR";
        const string originalValue = "original";

        Environment.SetEnvironmentVariable(testVarName, originalValue);
        try
        {
            var env = new IsolatedEnvironmentVariables
            {
                Tool          = OracleTool.Generic,
                SetVariables  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ClearVariables = [testVarName],
                PowerShellPreamble = string.Empty,
            };

            using (svc.ApplyScopedVariables(env))
            {
                Environment.GetEnvironmentVariable(testVarName).Should().BeNull("should be cleared in scope");
            }

            Environment.GetEnvironmentVariable(testVarName).Should().Be(originalValue, "should be restored after dispose");
        }
        finally
        {
            Environment.SetEnvironmentVariable(testVarName, null);
        }
    }

    // ── GenerateDiagnostics ───────────────────────────────────────────────────

    [Fact]
    public void GenerateDiagnostics_NoInjections_ReturnsEmptyReport()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(), snap);

        var report = svc.GenerateDiagnostics(ctx, postDeploymentSnapshot: null);
        report.Should().NotBeNull();
        report.PreambleInjectionCount.Should().Be(0);
        report.IsolatedToolInvocations.Should().BeEmpty();
    }

    [Fact]
    public void GenerateDiagnostics_AfterInjections_CountsCorrectly()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(), snap);

        svc.BuildIsolatedEnvironment(OracleTool.OUI, ctx);
        svc.BuildIsolatedEnvironment(OracleTool.WLST, ctx);
        svc.BuildIsolatedEnvironment(OracleTool.OPatch, ctx);

        var report = svc.GenerateDiagnostics(ctx, postDeploymentSnapshot: null);
        report.PreambleInjectionCount.Should().Be(3);
        report.IsolatedToolInvocations.Should().HaveCount(3);
    }

    [Fact]
    public void GenerateDiagnostics_WithDrift_PopulatesDriftReport()
    {
        var svc   = new EnvironmentIsolationService(MockLog());
        var snap  = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx   = svc.BuildContext(MakeConfig(), snap);

        // Simulate post-deployment snapshot with a new unexpected variable
        var postSnap = new EnvironmentSnapshot
        {
            Kind       = SnapshotKind.PostDeployment,
            SessionId  = ctx.SessionId,
            OracleHome = @"C:\Unexpected\Oracle",  // WEDM did not set this
        };

        svc.BuildIsolatedEnvironment(OracleTool.OUI, ctx); // OUI does not set ORACLE_HOME

        var report = svc.GenerateDiagnostics(ctx, postSnap);
        report.DriftDetected.Should().BeTrue();
        report.DriftReport!.UnexpectedFindings.Should().NotBeEmpty();
    }

    // ── AnalysePath ───────────────────────────────────────────────────────────

    [Fact]
    public void AnalysePath_JavaHomeRequired_MissingReported()
    {
        var svc  = new EnvironmentIsolationService(MockLog());
        var snap = svc.CaptureSnapshot(SnapshotKind.PreDeployment, Guid.NewGuid());
        var ctx  = svc.BuildContext(MakeConfig(javaHome: @"C:\Java\jdk21"), snap);

        // Path without Java
        var result = svc.AnalysePath(@"C:\Windows\System32", ctx);
        result.HasMissingRequired.Should().BeTrue();
        result.MissingRequired.Should().Contain($@"C:\Java\jdk21\bin");
    }
}
