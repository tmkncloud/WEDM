using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Windows;
using WEDM.Domain.Interfaces;
using WEDM.Engine.Opatch;
using WEDM.Engine.PowerShell;
using WEDM.Engine.ResponseFiles;
using WEDM.Engine.Validation;
using WEDM.Engine.Workflow;
using WEDM.Engine.Workflow.Steps;
using WEDM.Infrastructure.Deployment;
using WEDM.Infrastructure.Logging;
using WEDM.Infrastructure.Patching;
using WEDM.Infrastructure.Packaging;
using WEDM.Infrastructure.Registry;
using WEDM.Infrastructure.Security;
using WEDM.UI.Services;
using WEDM.UI.ViewModels;
using WEDM.UI.ViewModels.Wizard;
using DeploymentOrchestrator = WEDM.Application.Services.DeploymentOrchestrator;

namespace WEDM.UI;

/// <summary>
/// Application entry point and DI container bootstrapper.
/// Uses Microsoft.Extensions.Hosting for enterprise-grade service lifetime management.
///
/// Container registrations follow Clean Architecture layering:
///   Singleton: core services (logging, executor, registry)
///   Transient:  step executors, view models
///   Scoped:     deployment sessions (future phase)
/// </summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        StartupDiagnostics.Install(this);
        StartupDiagnostics.Trace("OnStartup", "begin");

        // First shown Window becomes Application.MainWindow. With OnMainWindowClose, closing the splash
        // would shut down the process before the real shell is shown — use explicit shutdown until wired.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var splash = new SplashWindow();
        splash.Show();
        StartupDiagnostics.Trace("Splash", "shown");

        try
        {
            StartupDiagnostics.Trace("Host", "CreateDefaultBuilder");
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(ConfigureServices)
                .Build();

            StartupDiagnostics.Trace("Host", "StartAsync");
            await _host.StartAsync();

            StartupDiagnostics.Trace("Host", "post-start delay");
            await System.Threading.Tasks.Task.Delay(450);

            StartupDiagnostics.ValidateServiceResolution(_host.Services);

            StartupDiagnostics.Trace("UI", "Resolve MainWindow");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();

            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            StartupDiagnostics.Trace("UI", "MainWindow.Show");
            mainWindow.Loaded += (_, _) =>
                StartupDiagnostics.Trace("UI", "MainWindow.Loaded — shell visual tree ready");
            mainWindow.Show();

            splash.Close();
            StartupDiagnostics.Trace("Splash", "closed after main shell");

            StartupDiagnostics.Trace("OnStartup", "complete");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.HandleStartupFailure(splash, ex);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ── Infrastructure ──────────────────────────────────────────────────
        services.AddSingleton<ILoggingService>(sp =>
            new SerilogLoggingService(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "WEDM", "logs")));

        services.AddSingleton<WindowsRegistryService>();
        services.AddSingleton<IDeploymentPlanAccessor, DeploymentPlanAccessor>();
        services.AddSingleton<IPowerShellExecutor, PowerShellExecutor>();

        services.AddSingleton<OpatchRunner>();
        services.AddSingleton<IPatchExecutionState, PatchExecutionState>();
        services.AddSingleton<IPatchReportWriter, PatchReportWriter>();

        services.AddSingleton<ILocalSecretVault, DpapiFileSecretVault>();
        services.AddSingleton<ISecurityComplianceEvaluator, SecurityComplianceEvaluator>();
        services.AddSingleton<ICertificateMaterialValidator, CertificatePkcs12Validator>();
        services.AddSingleton<ISecurityReportWriter, SecurityReportWriter>();

        services.AddSingleton<IOperationalTelemetrySink, NoOpOperationalTelemetrySink>();
        services.AddSingleton<IUpdateManifestReader, LocalUpdateManifestReader>();
        services.AddSingleton<IProductInfoProvider, AppProductInfoProvider>();
        services.AddSingleton<IAboutDialogService, AboutDialogService>();

        // ── Engine ────────────────────────────────────────────────────────────
        services.AddSingleton<ResponseFileGenerator>();
        services.AddSingleton<IValidationEngine, PrerequisiteValidator>();

        // Step executors (transient — stateless automation workers)
        services.AddTransient<ValidatePrerequisitesStep>();
        services.AddTransient<ValidatePayloadIntegrityStep>();
        services.AddTransient<CreateOracleFoldersStep>();
        services.AddTransient<CreateSnapshotStep>();
        services.AddTransient<InstallJdkStep>();
        services.AddTransient<InstallVcRedistStep>();
        services.AddTransient<ConfigureJavaHomeStep>();
        services.AddTransient<InstallWebLogicStep>();
        services.AddTransient<InstallFormsReportsStep>();
        services.AddTransient<InstallOhsWebTierStep>();
        services.AddTransient<RunRcuStep>();
        services.AddTransient<CreateDomainStep>();
        services.AddTransient<ConfigureAdminServerStep>();
        services.AddTransient<CreateManagedServersStep>();
        services.AddTransient<ConfigureNodeManagerStep>();
        services.AddTransient<CreateBootPropertiesStep>();
        services.AddTransient<ConfigureTnsnamesStep>();
        services.AddTransient<ConfigureFormsEnvStep>();
        services.AddTransient<ConfigureWebUtilStep>();
        services.AddTransient<ConfigureRegistryStep>();
        services.AddTransient<PostInstallValidationStep>();
        services.AddTransient<RegisterWindowsServicesStep>();
        services.AddTransient<CreateDesktopShortcutsStep>();
        services.AddTransient<GenerateDeploymentReportStep>();

        services.AddTransient<ValidatePatchHostPrereqsStep>();
        services.AddTransient<ValidateOpatchEnvironmentStep>();
        services.AddTransient<ValidatePatchStagingStep>();
        services.AddTransient<OpatchConflictCheckStep>();
        services.AddTransient<PrePatchOpatchInventorySnapshotStep>();
        services.AddTransient<DetectBlockingMiddlewareProcessesStep>();
        services.AddTransient<PrePatchMetadataSnapshotStep>();
        services.AddTransient<OpatchApplyPatchesStep>();
        services.AddTransient<OpatchPostApplyInventoryStep>();
        services.AddTransient<GeneratePatchComplianceReportStep>();
        services.AddTransient<RollbackOpatchApplyStep>();

        services.AddTransient<StartAdminServerForOnlineAutomationStep>();
        services.AddTransient<WlstOnlinePostBootAutomationStep>();
        services.AddTransient<ValidateNodeManagerReachabilityStep>();

        services.AddTransient<ValidateSecuritySecretsAndSslStep>();
        services.AddTransient<GenerateSecurityComplianceAuditStep>();

        services.AddSingleton<IStepExecutorFactory>(sp =>
        {
            var regWin = sp.GetRequiredService<RegisterWindowsServicesStep>();

            var executors = new Dictionary<string, IStepExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["ValidatePrerequisites"]      = sp.GetRequiredService<ValidatePrerequisitesStep>(),
                ["ValidatePayloadIntegrity"]   = sp.GetRequiredService<ValidatePayloadIntegrityStep>(),
                ["CreateOracleFolders"]        = sp.GetRequiredService<CreateOracleFoldersStep>(),
                ["CreateSnapshot"]             = sp.GetRequiredService<CreateSnapshotStep>(),
                ["InstallJDK"]                 = sp.GetRequiredService<InstallJdkStep>(),
                ["InstallVCRedist"]            = sp.GetRequiredService<InstallVcRedistStep>(),
                ["ConfigureJavaHome"]          = sp.GetRequiredService<ConfigureJavaHomeStep>(),
                ["InstallWebLogic"]            = sp.GetRequiredService<InstallWebLogicStep>(),
                ["InstallInfrastructure"]      = sp.GetRequiredService<InstallWebLogicStep>(),
                ["InstallFormsReports"]        = sp.GetRequiredService<InstallFormsReportsStep>(),
                ["InstallOHSWebTier"]          = sp.GetRequiredService<InstallOhsWebTierStep>(),
                ["RunRCU"]                     = sp.GetRequiredService<RunRcuStep>(),
                ["CreateDomain"]               = sp.GetRequiredService<CreateDomainStep>(),
                ["ConfigureAdminServer"]       = sp.GetRequiredService<ConfigureAdminServerStep>(),
                ["CreateManagedServers"]       = sp.GetRequiredService<CreateManagedServersStep>(),
                ["ConfigureNodeManager"]       = sp.GetRequiredService<ConfigureNodeManagerStep>(),
                ["CreateBootProperties"]       = sp.GetRequiredService<CreateBootPropertiesStep>(),
                ["ConfigureTnsnames"]          = sp.GetRequiredService<ConfigureTnsnamesStep>(),
                ["ConfigureFormsEnv"]          = sp.GetRequiredService<ConfigureFormsEnvStep>(),
                ["ConfigureWebUtil"]           = sp.GetRequiredService<ConfigureWebUtilStep>(),
                ["ConfigureRegistry"]          = sp.GetRequiredService<ConfigureRegistryStep>(),
                ["PostInstallValidation"]      = sp.GetRequiredService<PostInstallValidationStep>(),
                ["RegisterAdminService"]       = regWin,
                ["RegisterNodeMgrService"]     = regWin,
                ["CreateDesktopShortcuts"]     = sp.GetRequiredService<CreateDesktopShortcutsStep>(),
                ["GenerateDeploymentReport"]   = sp.GetRequiredService<GenerateDeploymentReportStep>(),

                ["ValidatePatchHostPrereqs"]           = sp.GetRequiredService<ValidatePatchHostPrereqsStep>(),
                ["ValidateOpatchEnvironment"]         = sp.GetRequiredService<ValidateOpatchEnvironmentStep>(),
                ["ValidatePatchStaging"]              = sp.GetRequiredService<ValidatePatchStagingStep>(),
                ["OpatchConflictCheck"]               = sp.GetRequiredService<OpatchConflictCheckStep>(),
                ["PrePatchOpatchInventorySnapshot"]   = sp.GetRequiredService<PrePatchOpatchInventorySnapshotStep>(),
                ["DetectBlockingMiddlewareProcesses"] = sp.GetRequiredService<DetectBlockingMiddlewareProcessesStep>(),
                ["PrePatchMetadataSnapshot"]          = sp.GetRequiredService<PrePatchMetadataSnapshotStep>(),
                ["OpatchApplyPatches"]                = sp.GetRequiredService<OpatchApplyPatchesStep>(),
                ["OpatchPostApplyInventory"]          = sp.GetRequiredService<OpatchPostApplyInventoryStep>(),
                ["GeneratePatchComplianceReport"]     = sp.GetRequiredService<GeneratePatchComplianceReportStep>(),

                ["StartAdminForOnlineAutomation"] = sp.GetRequiredService<StartAdminServerForOnlineAutomationStep>(),
                ["WlstOnlinePostBootAutomation"]   = sp.GetRequiredService<WlstOnlinePostBootAutomationStep>(),
                ["ValidateNodeManagerReachability"] = sp.GetRequiredService<ValidateNodeManagerReachabilityStep>(),

                ["ValidateSecuritySecretsAndSsl"]   = sp.GetRequiredService<ValidateSecuritySecretsAndSslStep>(),
                ["GenerateSecurityComplianceAudit"] = sp.GetRequiredService<GenerateSecurityComplianceAuditStep>(),
            };

            var rollback = new Dictionary<string, IStepExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["RollbackOpatchApply"] = sp.GetRequiredService<RollbackOpatchApplyStep>(),
            };

            IStepExecutor? Fallback(string name)
            {
                if (name.StartsWith("Register", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith("Service", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("RegisterAdminService", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("RegisterNodeMgrService", StringComparison.OrdinalIgnoreCase))
                    return regWin;
                return null;
            }

            return new StepExecutorFactory(executors, rollback, Fallback);
        });

        services.AddSingleton<IWorkflowOrchestrator, DeploymentWorkflowEngine>();

        // ── Application Layer ─────────────────────────────────────────────────
        services.AddSingleton<DeploymentOrchestrator>();

        // ── UI ────────────────────────────────────────────────────────────────
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<VersionSelectionViewModel>();
        services.AddTransient<PathConfigViewModel>();
        services.AddTransient<DatabaseConfigViewModel>();
        services.AddTransient<DomainConfigViewModel>();
        services.AddTransient<PrerequisiteViewModel>();
        services.AddTransient<PatchManagementViewModel>();
        services.AddTransient<SecurityComplianceViewModel>();
        services.AddTransient<ProductSystemHealthViewModel>();
        services.AddTransient<DeploymentProgressViewModel>();
        services.AddTransient<DeploymentSummaryViewModel>();

        services.AddSingleton<MainWindow>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
