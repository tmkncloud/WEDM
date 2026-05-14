using System.Diagnostics;
using System.ServiceProcess;
using System.Xml.Linq;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Automation;
using WEDM.Engine.ResponseFiles;
using WEDM.Infrastructure.Registry;

namespace WEDM.Engine.Workflow.Steps;

/// <summary>Runs WLST offline to materialise the domain on disk.</summary>
public sealed class CreateDomainStep : IStepExecutor
{
    private readonly IPowerShellExecutor _ps;
    private readonly ILoggingService     _log;

    public CreateDomainStep(IPowerShellExecutor ps, ILoggingService log)
    {
        _ps  = ps;
        _log = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var wlst = WlstDomainScriptBuilder.ResolveWlstCmd(config);
        if (!File.Exists(wlst))
            return StepExecutionResult.Fail($"WLST not found at '{wlst}'. Install Fusion Middleware / WebLogic first.");

        var scriptPath = Path.Combine(config.Paths.TempDirectory, $"wedm_create_domain_{config.Id:N}.py");
        Directory.CreateDirectory(config.Paths.TempDirectory);
        var py = WlstDomainScriptBuilder.BuildCreateDomainPy(config);
        await File.WriteAllTextAsync(scriptPath, py, cancellationToken);
        _log.Info($"WLST script written: {scriptPath}", "Domain");

        var wlstQ = "'" + wlst.Replace("'", "''", StringComparison.Ordinal) + "'";
        var pyQ   = "'" + scriptPath.Replace("'", "''", StringComparison.Ordinal) + "'";
        var body = $@"
$p = Start-Process -FilePath {wlstQ} -ArgumentList @({pyQ}) -Wait -PassThru -NoNewWindow
exit $(if ($null -eq $p) {{ 1 }} else {{ $p.ExitCode }})
";

        var result = await _ps.ExecuteCommandAsync(
            body.Trim(),
            workingDirectory: config.Paths.TempDirectory,
            runAsAdministrator: false,
            cancellationToken: cancellationToken,
            operationTimeout: TimeSpan.FromMinutes(90));

        sw.Stop();
        if (result.TimedOut)
            return StepExecutionResult.Fail("WLST domain creation timed out.", -2);
        if (result.ExitCode != 0)
            return StepExecutionResult.Fail($"WLST failed (exit {result.ExitCode}). Output: {result.Output}\nErrors: {result.Errors}", result.ExitCode);

        var domainHome = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);
        var cfgXml     = Path.Combine(domainHome, "config", "config.xml");
        if (!File.Exists(cfgXml))
            return StepExecutionResult.Fail($"Domain config.xml missing at '{cfgXml}'.");

        return StepExecutionResult.Ok($"Domain created at {domainHome}", sw.Elapsed);
    }
}

/// <summary>Validates AdminServer listen configuration in domain config.xml.</summary>
public sealed class ConfigureAdminServerStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public ConfigureAdminServerStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var cfgXml = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName, "config", "config.xml");
        if (!File.Exists(cfgXml))
            return Task.FromResult(StepExecutionResult.Fail($"config.xml not found: {cfgXml}"));

        try
        {
            var doc  = XDocument.Load(cfgXml);
            var admin = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "server" &&
                string.Equals((string?)e.Attribute("name"), config.Domain.AdminServerName, StringComparison.Ordinal));
            if (admin is null)
                return Task.FromResult(StepExecutionResult.Fail($"Admin server '{config.Domain.AdminServerName}' not found in config.xml."));

            var listen = admin.Descendants().FirstOrDefault(e => e.Name.LocalName == "listen-port")?.Value;
            if (int.TryParse(listen, out var p) && p == config.Domain.AdminPort)
            {
                sw.Stop();
                _log.Info($"AdminServer listen port verified: {p}", "Domain");
                return Task.FromResult(StepExecutionResult.Ok("AdminServer port matches deployment plan.", sw.Elapsed));
            }

            sw.Stop();
            return Task.FromResult(StepExecutionResult.Fail(
                $"AdminServer listen port in config.xml ({listen}) does not match configured port {config.Domain.AdminPort}."));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(StepExecutionResult.Fail($"Failed to parse config.xml: {ex.Message}", 1, ex));
        }
    }
}

/// <summary>Managed servers are created in the same WLST pass as the domain; this step validates presence.</summary>
public sealed class CreateManagedServersStep : IStepExecutor
{
    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (config.Domain.ManagedServers.Count == 0)
            return Task.FromResult(StepExecutionResult.Ok("No managed servers defined — skipped."));

        var cfgXml = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName, "config", "config.xml");
        if (!File.Exists(cfgXml))
            return Task.FromResult(StepExecutionResult.Fail("config.xml missing — domain not created."));

        var doc = XDocument.Load(cfgXml);
        foreach (var ms in config.Domain.ManagedServers)
        {
            var el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "server" &&
                string.Equals((string?)e.Attribute("name"), ms.Name, StringComparison.Ordinal));
            if (el is null)
                return Task.FromResult(StepExecutionResult.Fail($"Managed server '{ms.Name}' not found in config.xml."));
        }

        return Task.FromResult(StepExecutionResult.Ok($"Verified {config.Domain.ManagedServers.Count} managed server(s) in config.xml."));
    }
}

/// <summary>Enrols the domain in per-MW nodemanager.domains and ensures domain nodemanager.properties.</summary>
public sealed class ConfigureNodeManagerStep : IStepExecutor
{
    private readonly ILoggingService _log;

    public ConfigureNodeManagerStep(ILoggingService log) => _log = log;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var domainHome = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);
        var nmDir      = Path.Combine(domainHome, "nodemanager");
        Directory.CreateDirectory(nmDir);

        var nmProps = Path.Combine(nmDir, "nodemanager.properties");
        var lines = new List<string>
        {
            "# Generated by WEDM",
            $"ListenPort={config.Domain.NodeManager.Port}",
            $"ListenAddress={config.Domain.NodeManager.ListenAddress}",
            "CrashRecoveryEnabled=true",
            "NativeVersionEnabled=true",
            "DomainsFileRemoteSharingEnabled=false",
            "LogFileCount=10"
        };

        if (string.Equals(config.Domain.NodeManager.Type, "SSL", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("SecureListener=true");
            lines.Add("AuthenticationProtocol=TLSv1.2");
            lines.Add("# Replace DemoIdentityAndDemoTrust with CustomIdentity/Trust for production keystores");
            lines.Add("KeyStores=DemoIdentityAndDemoTrust");
        }

        if (config.DomainHardening.ProductionMode)
        {
            lines.Add("LogLevel=WARNING");
        }

        var ssl = config.Security.SslCertificates;
        if (!string.IsNullOrWhiteSpace(ssl.IdentityKeystorePath))
        {
            var idPath = Path.GetFullPath(ssl.IdentityKeystorePath);
            lines.Add($"CustomIdentityKeyStoreFileName={idPath}");
            var ext = Path.GetExtension(idPath).ToLowerInvariant();
            lines.Add(ext is ".p12" or ".pfx"
                ? "CustomIdentityKeyStoreType=PKCS12"
                : "CustomIdentityKeyStoreType=JKS");
            if (!string.IsNullOrWhiteSpace(ssl.IdentityPrivateKeyAlias))
                lines.Add($"CustomIdentityAlias={ssl.IdentityPrivateKeyAlias}");
            lines.Add("# CustomIdentityPrivateKeyPassPhrase must be supplied via secure Node Manager startup (not written here).");
        }

        if (!string.IsNullOrWhiteSpace(ssl.TrustKeystorePath))
        {
            var tPath = Path.GetFullPath(ssl.TrustKeystorePath);
            lines.Add($"CustomTrustKeyStoreFileName={tPath}");
            lines.Add("CustomTrustKeyStoreType=PKCS12");
            lines.Add("# CustomTrustKeyStorePassPhrase must be supplied via secure Node Manager startup (not written here).");
        }

        File.WriteAllLines(nmProps, lines);
        _log.Info($"nodemanager.properties written: {nmProps}", "NodeManager");

        var domainsFile = Path.Combine(config.Paths.MiddlewareHome, "wlserver", "common", "nodemanager", "nodemanager.domains");
        if (File.Exists(domainsFile))
        {
            var set = new HashSet<string>(File.ReadAllLines(domainsFile).Where(l => !string.IsNullOrWhiteSpace(l)),
                StringComparer.OrdinalIgnoreCase);
            set.Add(domainHome);
            File.WriteAllLines(domainsFile, set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            _log.Info($"Updated nodemanager.domains: {domainsFile}", "NodeManager");
        }
        else
        {
            _log.Warning($"Shared nodemanager.domains not found at '{domainsFile}' — Node Manager may use per-domain files only.", "NodeManager");
        }

        sw.Stop();
        return Task.FromResult(StepExecutionResult.Ok("Node Manager files updated.", sw.Elapsed));
    }
}

public sealed class CreateBootPropertiesStep : IStepExecutor
{
    private readonly ResponseFileGenerator _rsp;
    private readonly ILoggingService      _log;

    public CreateBootPropertiesStep(ResponseFileGenerator rsp, ILoggingService log)
    {
        _rsp = rsp;
        _log = log;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(config.Domain.AdminPassword))
            return Task.FromResult(StepExecutionResult.Fail("Admin password is required to generate boot.properties."));

        _rsp.GenerateBootProperties(config, config.Domain.AdminPassword);
        _log.Info("boot.properties generated for Admin and managed servers.", "Security");
        return Task.FromResult(StepExecutionResult.Ok("boot.properties created (will be encrypted on first server start)."));
    }
}

public sealed class ConfigureTnsnamesStep : IStepExecutor
{
    private readonly ResponseFileGenerator _rsp;

    public ConfigureTnsnamesStep(ResponseFileGenerator rsp) => _rsp = rsp;

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        _rsp.GenerateTnsnames(config);
        return Task.FromResult(StepExecutionResult.Ok("tnsnames.ora written under domain fmwconfig."));
    }
}

public sealed class ConfigureRegistryStep : IStepExecutor
{
    private readonly WindowsRegistryService _registry;
    private readonly ILoggingService        _log;

    public ConfigureRegistryStep(WindowsRegistryService registry, ILoggingService log)
    {
        _registry = registry;
        _log      = log;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var key = "KEY_WEDM_OracleMW";
        _registry.SetOracleHome(key, config.Paths.MiddlewareHome);
        _log.Info($"Oracle registry key {key} → ORACLE_HOME={config.Paths.MiddlewareHome}", "Registry");
        return Task.FromResult(StepExecutionResult.Ok("Oracle ORACLE_HOME registry entry updated."));
    }
}

public sealed class ConfigureFormsEnvStep : IStepExecutor
{
    private readonly ResponseFileGenerator _rsp;
    private readonly ILoggingService       _log;

    public ConfigureFormsEnvStep(ResponseFileGenerator rsp, ILoggingService log)
    {
        _rsp = rsp;
        _log = log;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (!config.ConfigureFormsReports)
            return Task.FromResult(StepExecutionResult.Ok("Forms configuration disabled — skipped."));

        try
        {
            _rsp.GenerateDefaultEnv(config, Array.Empty<string>());
            return Task.FromResult(StepExecutionResult.Ok("Default.env generated."));
        }
        catch (Exception ex)
        {
            _log.Warning($"Forms Default.env not written: {ex.Message}", "Forms");
            return Task.FromResult(StepExecutionResult.Ok("Forms paths not ready — skipped Default.env."));
        }
    }
}

public sealed class ConfigureWebUtilStep : IStepExecutor
{
    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
        => Task.FromResult(StepExecutionResult.Ok("WebUtil deployment is environment-specific — skipped in MVP."));
}

public sealed class RunRcuStep : IStepExecutor
{
    public Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
        => Task.FromResult(StepExecutionResult.Fail(
            "Automated RCU is not enabled in this build. Run RCU manually against your database, export a response file, " +
            "then set database.runRcu to false for silent WEDM runs, or extend RunRcuStep with your approved RCU property file."));
}

public sealed class PostInstallValidationStep : IStepExecutor
{
    private readonly IValidationEngine _validator;
    private readonly ILoggingService   _log;

    public PostInstallValidationStep(IValidationEngine validator, ILoggingService log)
    {
        _validator = validator;
        _log       = log;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        DeploymentStep step,
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var issues = new List<string>();

        if (!Directory.Exists(config.Paths.MiddlewareHome))
            issues.Add($"Middleware home missing: {config.Paths.MiddlewareHome}");

        var invXml = Path.Combine(config.Paths.OracleInventory, "ContentsXML", "inventory.xml");
        if (!File.Exists(invXml))
            issues.Add($"Oracle inventory metadata missing: {invXml}");

        var domainHome = Path.Combine(config.Paths.DomainBase, config.Domain.DomainName);
        if (!File.Exists(Path.Combine(domainHome, "config", "config.xml")))
            issues.Add($"Domain config missing under {domainHome}");

        var nmProps = Path.Combine(domainHome, "nodemanager", "nodemanager.properties");
        if (!File.Exists(nmProps))
            issues.Add("Domain nodemanager.properties missing.");

        var ports = await _validator.ValidatePortsAsync(config, cancellationToken);
        if (!ports.CanProceed)
            issues.Add("One or more required ports are not available.");

        if (issues.Count > 0)
        {
            var msg = string.Join("; ", issues);
            _log.Warning($"Post-install validation: {msg}", "Validation");
            return StepExecutionResult.Fail($"Post-install validation failed: {msg}", 50);
        }

        if (config.RegisterWindowsServices)
        {
            try
            {
                var nmHint = ServiceController.GetServices()
                    .Any(s => s.DisplayName.Contains("Node", StringComparison.OrdinalIgnoreCase) &&
                              s.DisplayName.Contains("Manager", StringComparison.OrdinalIgnoreCase));
                if (!nmHint)
                    _log.Warning("Node Manager Windows service not detected — run installNodeMgrSvc.cmd from WL_HOME\\server\\bin if required.", "Validation");
            }
            catch (Exception ex)
            {
                _log.Warning($"Service probe skipped: {ex.Message}", "Validation");
            }
        }

        sw.Stop();
        return StepExecutionResult.Ok("Post-install validation passed.", sw.Elapsed);
    }
}
