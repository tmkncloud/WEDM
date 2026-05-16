using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Versioning;

namespace WEDM.Engine.Validation;

/// <summary>Deep validation for Forms, Reports, and OHS configuration artifacts.</summary>
public sealed class FormsOhsDeepValidator
{
    public FormsOhsValidationReport Validate(DeploymentConfiguration config)
    {
        var adapter = WebLogicVersionAdapterFactory.For(config.WebLogicVersion);
        var findings = new List<FormsOhsFinding>();

        if (config.ConfigureFormsReports && adapter.SupportsFormsReports)
            ValidateForms(config, findings);
        if (config.Domain.FormsReports.InstallOhs && adapter.SupportsOhsWebTier)
            ValidateOhs(config, findings);

        return new FormsOhsValidationReport
        {
            WebLogicVersion = config.WebLogicVersion,
            Findings        = findings,
            CanProceed      = findings.All(f => f.Severity != FormsOhsSeverity.Fatal)
        };
    }

    private static void ValidateForms(DeploymentConfiguration config, List<FormsOhsFinding> findings)
    {
        var fr = config.Domain.FormsReports;
        if (string.IsNullOrWhiteSpace(fr.FormsPath))
            findings.Add(Finding("FormsPath", "Forms path is not configured.", FormsOhsSeverity.Error,
                "Set domain.formsReports.formsPath to the installed Forms instance directory."));
        else
        {
            var formsWeb = Path.Combine(fr.FormsPath, "bin", "formsweb.cfg");
            if (!File.Exists(formsWeb))
                findings.Add(Finding("formsweb.cfg", $"Missing: {formsWeb}", FormsOhsSeverity.Warning,
                    "Deploy formsweb.cfg from template or run InstallFormsReports step."));
            var defaultEnv = Path.Combine(fr.FormsPath, "bin", "default.env");
            if (!File.Exists(defaultEnv))
                findings.Add(Finding("default.env", $"Missing: {defaultEnv}", FormsOhsSeverity.Warning,
                    "Generate default.env via ConfigureFormsEnv step."));
        }

        if (string.IsNullOrWhiteSpace(fr.ReportsPath))
            findings.Add(Finding("REPORTS_PATH", "REPORTS_PATH is not set.", FormsOhsSeverity.Warning,
                "Configure REPORTS_PATH in Forms environment."));

        var tnsCandidates = new[]
        {
            Path.Combine(fr.FormsPath, "network", "admin"),
            Path.Combine(config.Paths.MiddlewareHome, "network", "admin"),
            config.Paths.TempDirectory
        }.Where(p => !string.IsNullOrWhiteSpace(p));

        if (!tnsCandidates.Any(p => File.Exists(Path.Combine(p, "tnsnames.ora"))))
            findings.Add(Finding("tnsnames.ora", "tnsnames.ora not found in Forms or middleware network/admin paths.", FormsOhsSeverity.Warning,
                "Configure TNS_ADMIN and deploy tnsnames.ora via ConfigureTnsnames step."));
    }

    private static void ValidateOhs(DeploymentConfiguration config, List<FormsOhsFinding> findings)
    {
        var ohsHome = Path.Combine(config.Paths.MiddlewareHome, "ohs");
        if (!Directory.Exists(ohsHome))
            findings.Add(Finding("OHS", $"OHS home not found at {ohsHome}", FormsOhsSeverity.Warning,
                "Install OHS Web Tier or set middleware home correctly."));

        var modules = Path.Combine(ohsHome, "modules");
        if (Directory.Exists(ohsHome) && !Directory.Exists(modules))
            findings.Add(Finding("OHS modules", $"OHS modules directory missing: {modules}", FormsOhsSeverity.Warning,
                "Verify OHS Web Tier installation completed."));
    }

    private static FormsOhsFinding Finding(string area, string message, FormsOhsSeverity severity, string remediation)
        => new() { Area = area, Message = message, Severity = severity, Remediation = remediation };
}

public sealed class FormsOhsValidationReport
{
    public WebLogicVersion WebLogicVersion { get; init; }
    public IReadOnlyList<FormsOhsFinding> Findings { get; init; } = [];
    public bool CanProceed { get; init; }
}

public sealed class FormsOhsFinding
{
    public string Area { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public FormsOhsSeverity Severity { get; init; }
    public string Remediation { get; init; } = string.Empty;
}

public enum FormsOhsSeverity { Info, Warning, Error, Fatal }
