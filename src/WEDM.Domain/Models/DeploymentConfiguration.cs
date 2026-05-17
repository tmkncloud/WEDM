using System.Text.Json.Serialization;
using WEDM.Domain.Enums;

namespace WEDM.Domain.Models;

/// <summary>
/// Root deployment configuration model.
/// Represents a complete, serialisable deployment plan that drives the entire automation engine.
/// Designed for external configuration (JSON/YAML), GUI-driven wizard, and silent deployment.
/// </summary>
public sealed class DeploymentConfiguration
{
    // ── Identity ────────────────────────────────────────────────────────────

    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Deployment";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = System.Environment.UserName;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "Production"; // DEV | UAT | PROD

    // ── Version & Platform ───────────────────────────────────────────────────

    [JsonPropertyName("webLogicVersion")]
    public WebLogicVersion WebLogicVersion { get; set; } = WebLogicVersion.WLS_12c;

    [JsonPropertyName("platform")]
    public DeploymentPlatform Platform { get; set; } = DeploymentPlatform.WindowsServer;

    [JsonPropertyName("mode")]
    public DeploymentMode Mode { get; set; } = DeploymentMode.Interactive;

    [JsonPropertyName("components")]
    public InstallationComponents Components { get; set; } = InstallationComponents.AllWindows;

    // ── Paths ────────────────────────────────────────────────────────────────

    [JsonPropertyName("paths")]
    public PathConfiguration Paths { get; set; } = new();

    // ── Java ────────────────────────────────────────────────────────────────

    [JsonPropertyName("java")]
    public JavaConfiguration Java { get; set; } = new();

    // ── Database / RCU ──────────────────────────────────────────────────────

    [JsonPropertyName("database")]
    public DatabaseConfiguration Database { get; set; } = new();

    // ── Domain ───────────────────────────────────────────────────────────────

    [JsonPropertyName("domain")]
    public DomainConfiguration Domain { get; set; } = new();

    // ── Network ──────────────────────────────────────────────────────────────

    [JsonPropertyName("network")]
    public NetworkConfiguration Network { get; set; } = new();

    // ── Security ─────────────────────────────────────────────────────────────

    [JsonPropertyName("security")]
    public SecurityConfiguration Security { get; set; } = new();

    // ── Payload ──────────────────────────────────────────────────────────────

    [JsonPropertyName("payloadBasePath")]
    public string PayloadBasePath { get; set; } = @"D:\WEDM";

    // ── Options ──────────────────────────────────────────────────────────────

    [JsonPropertyName("skipSoftwareUpdates")]
    public bool SkipSoftwareUpdates { get; set; } = true;

    [JsonPropertyName("declineSecurityUpdates")]
    public bool DeclineSecurityUpdates { get; set; } = true;

    [JsonPropertyName("enableRollback")]
    public bool EnableRollback { get; set; } = true;

    [JsonPropertyName("createDesktopShortcuts")]
    public bool CreateDesktopShortcuts { get; set; } = true;

    [JsonPropertyName("registerWindowsServices")]
    public bool RegisterWindowsServices { get; set; } = true;

    [JsonPropertyName("configureFormsReports")]
    public bool ConfigureFormsReports { get; set; } = true;

    /// <summary>Absolute path to JDK Windows installer (.msi or vendor silent .exe).</summary>
    [JsonPropertyName("jdkInstallerPath")]
    public string JdkInstallerPath { get; set; } = string.Empty;

    /// <summary>Absolute path to VC++ x64 redistributable (e.g. vc_redist.x64.exe).</summary>
    [JsonPropertyName("vcRedistX64InstallerPath")]
    public string VcRedistX64InstallerPath { get; set; } = string.Empty;

    [JsonPropertyName("vcRedistX86InstallerPath")]
    public string VcRedistX86InstallerPath { get; set; } = string.Empty;

    [JsonPropertyName("infrastructureInstallerPath")]
    public string InfrastructureInstallerPath { get; set; } = string.Empty;

    [JsonPropertyName("webLogicInstallerPath")]
    public string WebLogicInstallerPath { get; set; } = string.Empty;

    [JsonPropertyName("formsInstallerPath")]
    public string FormsInstallerPath { get; set; } = string.Empty;

    [JsonPropertyName("webTierInstallerPath")]
    public string WebTierInstallerPath { get; set; } = string.Empty;

    [JsonPropertyName("webUtilRootPath")]
    public string WebUtilRootPath { get; set; } = string.Empty;

    [JsonPropertyName("localPayload")]
    public LocalPayloadResolutionSnapshot LocalPayload { get; set; } = new();

    /// <summary>Maximum wall-clock time for Oracle OUI java -jar silent install before termination.</summary>
    [JsonPropertyName("ouiInstallTimeoutMinutes")]
    public int OuiInstallTimeoutMinutes { get; set; } = 240;

    /// <summary>Oracle OPatch / PSU automation.</summary>
    [JsonPropertyName("patches")]
    public PatchConfiguration Patches { get; set; } = new();

    /// <summary>DEV / SIT / UAT / PROD preset — drives defaults for hardening and online automation.</summary>
    [JsonPropertyName("deploymentEnvironment")]
    public DeploymentEnvironmentKind DeploymentEnvironment { get; set; } = DeploymentEnvironmentKind.Dev;

    [JsonPropertyName("domainHardening")]
    public DomainHardeningConfiguration DomainHardening { get; set; } = new();

    [JsonPropertyName("domainOnlineAutomation")]
    public DomainOnlineAutomationConfiguration DomainOnlineAutomation { get; set; } = new();

    /// <summary>JDK/VC++ detection, download cache, and middleware payload rules.</summary>
    [JsonPropertyName("payloadAcquisition")]
    public PayloadAcquisitionConfiguration PayloadAcquisition { get; set; } = new();

    /// <summary>Oracle inventory conflict detection, force clean install, and retry isolation.</summary>
    [JsonPropertyName("oracleLifecycle")]
    public OracleLifecycleConfiguration OracleLifecycle { get; set; } = new();

    // ── Runtime-only (session-scoped, never serialised) ─────────────────────

    /// <summary>
    /// Per-attempt installer execution context set by <see cref="WEDM.Domain.Interfaces.IInstallRetryIsolationService"/>
    /// before each OUI step invocation.  Null on first attempt when isolation is disabled.
    /// Not serialised — rebuilt fresh on each engine startup / retry.
    /// </summary>
    [JsonIgnore]
    public InstallerExecutionContext? CurrentInstallerContext { get; set; }

    /// <summary>Remediation reports produced during this deployment session (auto-repair, pre-flight, retry).</summary>
    [JsonIgnore]
    public List<OracleRemediationReport> RemediationReports { get; } = [];

    /// <summary>Latest assessment from the remediation engine (not persisted to JSON plan files).</summary>
    [JsonIgnore]
    public OracleRemediationAssessment? LastRemediationAssessment { get; set; }

    /// <summary>Inventory bootstrap reports for this deployment session.</summary>
    [JsonIgnore]
    public List<OracleInventoryBootstrapReport> BootstrapReports { get; } = [];

    [JsonIgnore]
    public InventoryBootstrapAssessment? LastBootstrapAssessment { get; set; }

    /// <summary>Tracks remediation execution per install step attempt (prevents infinite cleanup loops).</summary>
    [JsonIgnore]
    public OracleRemediationSessionState RemediationSession { get; } = new();

    /// <summary>
    /// Oracle rollback report accumulated by Oracle-aware rollback executors during a rollback pass.
    /// Each Oracle rollback executor (OracleInstallRollbackExecutor, OracleFormsReportsRollbackExecutor, etc.)
    /// writes its per-step results here.  The workflow engine attaches this to
    /// <see cref="RollbackSummary.OracleDetails"/> after the full rollback pass completes.
    /// Null until the first Oracle rollback executor runs.
    /// Not serialised — runtime-only.
    /// </summary>
    [JsonIgnore]
    public OracleRollbackReport? OracleRollback { get; set; }

    /// <summary>
    /// Per-session environment isolation context.
    /// Built once at session start by <see cref="WEDM.Domain.Interfaces.IEnvironmentIsolationService.BuildContext"/>
    /// and stored here so that all step executors can access the sanitized JavaHome, PATH, TempRoot, and
    /// per-tool isolation preambles without re-reading machine environment state.
    ///
    /// Null when the environment isolation subsystem is not initialised (e.g. in unit tests that
    /// construct DeploymentConfiguration directly without the full DI stack).  Step executors that
    /// consume this must guard against null and fall back to ambient environment variables when absent.
    ///
    /// Not serialised — runtime-only; rebuilt from config on each engine startup.
    /// </summary>
    [JsonIgnore]
    public DeploymentEnvironmentContext? EnvironmentContext { get; set; }
}

// ── Sub-models ─────────────────────────────────────────────────────────────────

public sealed class PathConfiguration
{
    public string OracleRoot        { get; set; } = @"C:\Oracle";
    public string MiddlewareHome    { get; set; } = @"C:\Oracle\Oracle_MW";
    public string DomainBase        { get; set; } = @"C:\Oracle\Oracle_MW\user_projects\domains";
    public string OracleInventory   { get; set; } = @"C:\Oracle\oraInventory";
    public string TempDirectory     { get; set; } = @"C:\Oracle\Temp";
    public string LogDirectory      { get; set; } = @"C:\Oracle\WEDM\logs";
    public string ReportsDirectory  { get; set; } = @"C:\Oracle\WEDM\reports";
    public string SnapshotDirectory { get; set; } = @"C:\Oracle\WEDM\snapshots";
}

public sealed class JavaConfiguration
{
    public string InstallDirectory   { get; set; } = @"C:\Program Files\Java";
    public string JdkVersion         { get; set; } = "1.8.0_202";   // resolved per WLS version
    public string JavaHome           { get; set; } = string.Empty;   // set after install
    public int    HeapSizeMb         { get; set; } = 1024;
    public bool   AutoDetectExisting { get; set; } = true;

    [JsonPropertyName("lastInstallationDiagnostics")]
    public JdkInstallationDiagnostics? LastInstallationDiagnostics { get; set; }
}

public sealed class DatabaseConfiguration
{
    public string  Host           { get; set; } = "localhost";
    public int     Port           { get; set; } = 1521;
    public string  ServiceName    { get; set; } = "orcl";
    public string  SysUsername    { get; set; } = "system";
    public string  SysPassword    { get; set; } = string.Empty;   // encrypted at rest
    public string  SchemaPrefix   { get; set; } = "DEV";
    public string  SchemaPassword { get; set; } = string.Empty;   // encrypted at rest
    public bool    RunRcu         { get; set; } = false;
    public string  RcuPath        { get; set; } = string.Empty;   // resolved at runtime
    public string  NlsCharset     { get; set; } = "AL32UTF8";
}

public sealed class DomainConfiguration
{
    public string            DomainName        { get; set; } = "wls_domain";
    public DomainTopology    Topology          { get; set; } = DomainTopology.Standard;
    public string            AdminServerName   { get; set; } = "AdminServer";
    public int               AdminPort         { get; set; } = 7001;
    public int               AdminSslPort      { get; set; } = 7002;
    public string            AdminUsername     { get; set; } = "weblogic";
    public string            AdminPassword     { get; set; } = string.Empty;  // encrypted
    public string            Machine           { get; set; } = "AdminServerMachine";
    public List<ManagedServerDefinition> ManagedServers { get; set; } = new();
    public NodeManagerConfiguration NodeManager { get; set; } = new();
    public FormsReportsConfiguration FormsReports { get; set; } = new();
}

public sealed class ManagedServerDefinition
{
    public string     Name           { get; set; } = "WLS_FORMS";
    public ServerType Type           { get; set; } = ServerType.ManagedServer;
    public int        Port           { get; set; } = 9001;
    public int        SslPort        { get; set; } = 9002;
    public string     ClusterName    { get; set; } = string.Empty;
    public bool       RegisterService { get; set; } = true;
    public int        JvmHeapMb      { get; set; } = 512;
    public int        JvmMaxHeapMb   { get; set; } = 1024;
}

public sealed class NodeManagerConfiguration
{
    public int    Port             { get; set; } = 5556;
    public string ListenAddress    { get; set; } = "localhost";
    public string Type             { get; set; } = "Plain";         // Plain | SSL
    public bool   RegisterService  { get; set; } = true;
    public string ServiceName      { get; set; } = "WLS NodeManager";
}

public sealed class FormsReportsConfiguration
{
    public bool   Install          { get; set; } = true;
    public string FormsPath        { get; set; } = string.Empty;
    public string ReportsPath      { get; set; } = string.Empty;
    public string NlsLang          { get; set; } = "AMERICAN_AMERICA.AR8MSWIN1256";
    public string DbConnectString  { get; set; } = string.Empty;
    public bool   InstallWebUtil   { get; set; } = true;
    public bool   InstallOhs       { get; set; } = true;
    public int    OhsPort          { get; set; } = 7777;
}

public sealed class NetworkConfiguration
{
    public string Hostname { get; set; } = Environment.MachineName;
    public string IpAddress { get; set; } = "127.0.0.1";
    public List<int> BlockedPorts { get; set; } = new();
}

public sealed class SecurityConfiguration
{
    public bool   UseEncryptedPasswords  { get; set; } = true;
    public string EncryptionKeyReference { get; set; } = "WEDM_DPAPI";  // DPAPI key handle
    public bool   EnableSsl              { get; set; } = false;
    public string KeystorePath           { get; set; } = string.Empty;
    public string KeystorePassword       { get; set; } = string.Empty;

    [JsonPropertyName("secrets")]
    public SecretsManagementConfiguration Secrets { get; set; } = new();

    [JsonPropertyName("sslCertificates")]
    public SslCertificateConfiguration SslCertificates { get; set; } = new();
}
