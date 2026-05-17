using System.Text.Json;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Decommissioning;
using WEDM.Engine.Discovery.Parsers;
using WEDM.Engine.OracleInventory;

namespace WEDM.Engine.OracleInventoryBootstrap;

public sealed class OracleInventoryBootstrapService : IOracleInventoryBootstrapService
{
    private readonly IOracleInventoryAnalyzer      _analyzer;
    private readonly IOracleInventoryPathResolver  _paths;
    private readonly IOracleInventorySkeletonFactory _skeleton;
    private readonly IOracleInventoryBootstrapValidator _validator;
    private readonly IOracleInventoryBootstrapReportBuilder _reports;
    private readonly IOracleProcessManager         _processes;
    private readonly IOracleInventoryService       _inventory;
    private readonly ILoggingService               _log;

    public OracleInventoryBootstrapService(
        IOracleInventoryAnalyzer analyzer,
        IOracleInventoryPathResolver paths,
        IOracleInventorySkeletonFactory skeleton,
        IOracleInventoryBootstrapValidator validator,
        IOracleInventoryBootstrapReportBuilder reports,
        IOracleProcessManager processes,
        IOracleInventoryService inventory,
        ILoggingService log)
    {
        _analyzer  = analyzer;
        _paths     = paths;
        _skeleton  = skeleton;
        _validator = validator;
        _reports   = reports;
        _processes = processes;
        _inventory = inventory;
        _log       = log;
    }

    public InventoryBootstrapAssessment Assess(DeploymentConfiguration config)
    {
        var pointer = _paths.Resolve(config);
        var root    = pointer.CentralInventoryRoot;
        var analysis = _analyzer.Analyze(root, config.Paths.MiddlewareHome);
        var safety   = EvaluateSafety(config, analysis, root);

        var requiresBootstrap = analysis.State is OracleCentralInventoryState.Missing
            or OracleCentralInventoryState.BootstrapRequired
            || NeedsBootstrap(root);

        if (requiresBootstrap && analysis.State == OracleCentralInventoryState.Missing && !string.IsNullOrWhiteSpace(root))
            analysis.State = OracleCentralInventoryState.BootstrapRequired;

        var strategy = config.OracleLifecycle.BootstrapVersionStrategy;
        var plan     = BuildPlan(config, root, strategy, safety.IsSafe);

        var assessment = new InventoryBootstrapAssessment
        {
            State             = analysis.State,
            RequiresBootstrap = requiresBootstrap && NeedsBootstrap(root),
            CanAutoBootstrap  = safety.IsSafe && plan.CanExecute && requiresBootstrap,
            Safety            = safety,
            Plan              = plan,
        };

        config.LastBootstrapAssessment = assessment;
        return assessment;
    }

    public bool ShouldAutoBootstrap(DeploymentConfiguration config, InventoryBootstrapAssessment assessment)
    {
        var lc = config.OracleLifecycle;
        if (!lc.EnableAutomaticInventoryBootstrap || !assessment.RequiresBootstrap)
            return false;

        if (!lc.AllowBootstrapOnCleanInstall && !lc.AllowBootstrapAfterDecommission)
            return false;

        if (lc.BootstrapDryRun)
            return false;

        return assessment.CanAutoBootstrap;
    }

    public async Task<OracleInventoryBootstrapResult> EnsureInventoryReadyAsync(
        DeploymentConfiguration config,
        InventoryBootstrapExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var assessment = config.LastBootstrapAssessment ?? Assess(config);
        var plan         = assessment.Plan ?? BuildPlan(config, OracleInventoryPathResolver.ResolveInventoryRoot(config),
            config.OracleLifecycle.BootstrapVersionStrategy, assessment.Safety.IsSafe);

        if (!assessment.RequiresBootstrap)
        {
            _log.Info("[InventoryBootstrap] Central inventory already present — no bootstrap required.", "InventoryBootstrap");
            return new OracleInventoryBootstrapResult
            {
                Success                 = true,
                ContinuationRecommended = true,
                Report                  = _reports.Build(assessment, plan, [], [], null, null, options, true),
            };
        }

        if (!options.DryRun && !ShouldAutoBootstrap(config, assessment) && !assessment.Safety.IsSafe)
        {
            return new OracleInventoryBootstrapResult
            {
                Success = false,
                Report  = _reports.Build(assessment, plan, [], [], null, null, options, false),
            };
        }

        var dryRun = options.DryRun || config.OracleLifecycle.BootstrapDryRun;
        var createdDirs  = new List<string>();
        var writtenFiles = new List<string>();
        var checkpoint   = LoadCheckpoint(config) ?? new InventoryBootstrapCheckpoint { DeploymentId = config.Id };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!dryRun && File.Exists(plan.InventoryXmlPath))
            {
                var existing = OracleInventoryXmlParser.ParseInventoryXml(plan.InventoryXmlPath);
                if (OracleCentralInventoryClassifier.IsXmlReadable(existing.InventoryState))
                {
                    _log.Info("[InventoryBootstrap] Valid inventory.xml already exists — idempotent skip.", "InventoryBootstrap");
                    var pointerSkip = WritePointer(config, options.PointerScope, dryRun: false);
                    var validationSkip = _validator.Validate(plan.InventoryRoot, plan.InventoryXmlPath);
                    var reportSkip = _reports.Build(assessment, plan, [], [plan.InventoryXmlPath], pointerSkip, validationSkip, options, true);
                    config.BootstrapReports.Add(reportSkip);
                    return new OracleInventoryBootstrapResult { Success = true, ContinuationRecommended = validationSkip.Passed, Report = reportSkip };
                }

                if (existing.InventoryState == OracleCentralInventoryState.Corrupted)
                {
                    _log.Error("[InventoryBootstrap] Refusing to overwrite corrupt inventory.xml.", category: "InventoryBootstrap");
                    return Failed(config, assessment, plan, options, "Corrupt inventory.xml present — manual repair required.");
                }
            }

            foreach (var dir in plan.DirectoriesToCreate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = $"dir:{dir}";
                if (checkpoint.CompletedSteps.Contains(key))
                    continue;

                if (dryRun)
                {
                    _log.Info($"[InventoryBootstrap:dry-run] Would create directory: {dir}", "InventoryBootstrap");
                }
                else
                {
                    Directory.CreateDirectory(dir);
                    createdDirs.Add(dir);
                    checkpoint.CompletedSteps.Add(key);
                    PersistCheckpoint(config, checkpoint);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (dryRun)
            {
                _log.Info($"[InventoryBootstrap:dry-run] Would write: {plan.InventoryXmlPath}", "InventoryBootstrap");
                writtenFiles.Add(plan.InventoryXmlPath);
            }
            else
            {
                var xml = _skeleton.BuildInventoryXml(config, plan.Strategy);
                await File.WriteAllTextAsync(plan.InventoryXmlPath, xml, cancellationToken);
                writtenFiles.Add(plan.InventoryXmlPath);

                var metaPath = Path.Combine(plan.InventoryRoot, "wedm-bootstrap.json");
                var meta = JsonSerializer.Serialize(new
                {
                    bootstrappedAt = DateTimeOffset.UtcNow,
                    deploymentId   = config.Id,
                    versionProfile = plan.VersionProfile,
                    strategy       = plan.Strategy.ToString(),
                }, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(metaPath, meta, cancellationToken);
                writtenFiles.Add(metaPath);
            }

            var pointer = WritePointer(config, options.PointerScope, dryRun);
            if (pointer is not null && !string.IsNullOrWhiteSpace(pointer.PointerFilePath))
                writtenFiles.Add(pointer.PointerFilePath);

            InventoryBootstrapValidationResult? validation = null;
            if (!dryRun)
            {
                validation = _validator.Validate(plan.InventoryRoot, plan.InventoryXmlPath);
                if (!validation.Passed)
                    return Failed(config, assessment, plan, options, string.Join("; ", validation.Findings));
            }

            var success = dryRun || (validation?.Passed ?? false);
            var report  = _reports.Build(assessment, plan, createdDirs, writtenFiles, pointer, validation, options, success);
            config.BootstrapReports.Add(report);

            _log.Info($"[InventoryBootstrap] Completed (dryRun={dryRun}, success={success}).", "InventoryBootstrap");
            return new OracleInventoryBootstrapResult
            {
                Success                 = success,
                ContinuationRecommended = success,
                Report                  = report,
            };
        }
        catch (Exception ex)
        {
            _log.Error($"[InventoryBootstrap] Failed: {ex.Message}", category: "InventoryBootstrap");
            return Failed(config, assessment, plan, options, ex.Message);
        }
    }

    private InventoryBootstrapSafetyResult EvaluateSafety(
        DeploymentConfiguration config,
        OracleInventoryAnalysis analysis,
        string inventoryRoot)
    {
        var reasons  = new List<string>();
        var blocking = new List<string>();

        if (analysis.State == OracleCentralInventoryState.Corrupted)
            blocking.Add("Central inventory.xml exists but is corrupt — bootstrap refused.");

        if (analysis.LockPresent)
            blocking.Add($"Active inventory lock: {analysis.LockFilePath}");

        if (analysis.Homes.Any(h => h.PathExists && !h.IsStale))
            blocking.Add("Registered Oracle home(s) already exist in central inventory.");

        var active = _processes.DetectMiddlewareProcesses();
        if (active.Count > 0)
            blocking.Add($"{active.Count} active middleware process(es) detected.");

        var locks = _inventory.DetectLocks(inventoryRoot);
        if (locks.Any(l => !l.IsStale))
            blocking.Add("Active inventory lock files detected under inventory root.");

        if (blocking.Count == 0)
            reasons.Add("Clean-install conditions detected — bootstrap is safe.");

        return new InventoryBootstrapSafetyResult
        {
            IsSafe           = blocking.Count == 0,
            Reasons          = reasons,
            BlockingReasons  = blocking,
        };
    }

    private static InventoryBootstrapPlan BuildPlan(
        DeploymentConfiguration config,
        string root,
        BootstrapVersionStrategy strategy,
        bool canExecute)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return new InventoryBootstrapPlan
            {
                CanExecute = false,
                Summary    = "Oracle inventory path is not configured.",
            };
        }

        var contentsXml = Path.Combine(root, "ContentsXML");
        var xmlPath     = Path.Combine(contentsXml, "inventory.xml");
        var dirs        = new List<string> { root, contentsXml, Path.Combine(root, "logs"), Path.Combine(root, "locks") };

        var factory = new OracleInventorySkeletonFactory();
        return new InventoryBootstrapPlan
        {
            InventoryRoot        = root,
            InventoryXmlPath     = xmlPath,
            DirectoriesToCreate  = dirs,
            FilesToWrite         = [xmlPath],
            Strategy             = strategy,
            VersionProfile       = factory.GetVersionProfile(config, strategy),
            CanExecute           = canExecute,
            Summary              = $"Bootstrap central inventory at '{root}' with {strategy} version strategy.",
        };
    }

    private static bool NeedsBootstrap(string inventoryRoot)
    {
        if (string.IsNullOrWhiteSpace(inventoryRoot))
            return false;

        var xml = Path.Combine(inventoryRoot, "ContentsXML", "inventory.xml");
        return !File.Exists(xml);
    }

    private InventoryPointerContext? WritePointer(DeploymentConfiguration config, InventoryPointerScope scope, bool dryRun)
    {
        var pointer = _paths.Resolve(config, scope);
        if (dryRun)
            return pointer;

        var dir = Path.GetDirectoryName(pointer.PointerFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var content = $"inventory_loc={pointer.CentralInventoryRoot}\ninst_group=Administrators\n";
        File.WriteAllText(pointer.PointerFilePath, content);
        return pointer;
    }

    private OracleInventoryBootstrapResult Failed(
        DeploymentConfiguration config,
        InventoryBootstrapAssessment assessment,
        InventoryBootstrapPlan plan,
        InventoryBootstrapExecutionOptions options,
        string error)
    {
        var report = _reports.Build(assessment, plan, [], [], null, null, options, false);
        report.Errors.Add(error);
        config.BootstrapReports.Add(report);
        return new OracleInventoryBootstrapResult { Success = false, Report = report };
    }

    private static void PersistCheckpoint(DeploymentConfiguration config, InventoryBootstrapCheckpoint checkpoint)
    {
        try
        {
            var dir = config.Paths.ReportsDirectory;
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            checkpoint.LastUpdated = DateTimeOffset.UtcNow;
            File.WriteAllText(
                Path.Combine(dir, $"bootstrap-checkpoint-{config.Id:N}.json"),
                JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    private static InventoryBootstrapCheckpoint? LoadCheckpoint(DeploymentConfiguration config)
    {
        try
        {
            var path = Path.Combine(config.Paths.ReportsDirectory, $"bootstrap-checkpoint-{config.Id:N}.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<InventoryBootstrapCheckpoint>(File.ReadAllText(path));
        }
        catch { return null; }
    }
}
