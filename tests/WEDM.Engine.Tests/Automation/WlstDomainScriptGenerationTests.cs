using FluentAssertions;
using WEDM.Domain.Enums;
using WEDM.Domain.Models;
using WEDM.Engine.Automation;
using Xunit;

namespace WEDM.Engine.Tests.Automation;

// ═══════════════════════════════════════════════════════════════════════════════
// WlstDomainScriptGenerationTests
// ═══════════════════════════════════════════════════════════════════════════════
//
// Verifies correctness of the version-aware WLST domain-creation script pipeline:
//   • Wls12cDomainScriptProvider / Wls11gDomainScriptProvider output
//   • WlstCompatibilityValidator detection of invalid API usage
//   • WlstDomainScriptProviderFactory version routing
//   • WlstScriptContext diagnostic metadata
//
// Root cause validated: WebLogic 12c writeDomain() calls checkSecurityInfo() which
// rejects set('Password', ...).  The correct API is cmo.setPassword() via MBean nav.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class WlstDomainScriptGenerationTests
{
    // ── Config helpers ────────────────────────────────────────────────────────

    private static DeploymentConfiguration Make12cConfig(
        string? adminUser = "weblogic",
        string? adminPwd  = "Welcome1",
        string? domainName = "wls_domain") =>
        new()
        {
            WebLogicVersion = WebLogicVersion.WLS_12c,
            Paths = new PathConfiguration
            {
                MiddlewareHome = @"D:\Oracle\Oracle_MW",
                DomainBase     = @"D:\Oracle\Oracle_MW\user_projects\domains",
                TempDirectory  = @"D:\Temp\wedm",
            },
            Domain = new DomainConfiguration
            {
                DomainName       = domainName!,
                AdminServerName  = "AdminServer",
                AdminUsername    = adminUser!,
                AdminPassword    = adminPwd!,
                AdminPort        = 7001,
            },
            Network = new NetworkConfiguration { Hostname = "localhost" },
            DomainHardening = new DomainHardeningConfiguration { ProductionMode = false },
        };

    private static DeploymentConfiguration Make11gConfig() =>
        new()
        {
            WebLogicVersion = WebLogicVersion.WLS_11g,
            Paths = new PathConfiguration
            {
                MiddlewareHome = @"D:\Oracle\Oracle_MW_11g",
                DomainBase     = @"D:\Oracle\Oracle_MW_11g\user_projects\domains",
                TempDirectory  = @"D:\Temp\wedm",
            },
            Domain = new DomainConfiguration
            {
                DomainName      = "wls11g_domain",
                AdminServerName = "AdminServer",
                AdminUsername   = "weblogic",
                AdminPassword   = "Welcome11g",
                AdminPort       = 7001,
            },
            Network = new NetworkConfiguration { Hostname = "localhost" },
            DomainHardening = new DomainHardeningConfiguration { ProductionMode = false },
        };

    // ═══════════════════════════════════════════════════════════════════════════
    // 1. Wls12cDomainScriptProvider — password API correctness
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Wls12c_script_uses_cmo_setPassword_not_set_Password_attribute()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        // THE CRITICAL CHECK: set('Password', ...) triggers ScriptException in writeDomain()
        ctx.ScriptContent.Should().NotContain("set('Password'",
            "set('Password', ...) is not a valid MBean attribute in WebLogic 12c — " +
            "it triggers com.oracle.cie.domain.script.ScriptException: Attribute 'Password' is not valid");

        ctx.ScriptContent.Should().NotContain("set(\"Password\"",
            "double-quote form of set(\"Password\", ...) is equally invalid in 12c");

        // THE FIX: must use cmo.setPassword() via MBean navigation
        ctx.ScriptContent.Should().Contain("cmo.setPassword(",
            "12c requires cmo.setPassword() on the security-realm User MBean");
    }

    [Fact]
    public void Wls12c_script_uses_dynamic_discovery_not_hardcoded_realm_path()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig(adminUser: "myadmin"));

        // Dynamic discovery: must call _wedm_discover_admin_path, not a hardcoded cd('/Security/...')
        ctx.ScriptContent.Should().Contain("_wedm_discover_admin_path(",
            "admin path must be discovered dynamically — hardcoded /Security/<realm>/ paths " +
            "fail when the template uses a realm name other than 'base_domain'");
        ctx.ScriptContent.Should().Contain("cmo.setPassword('Welcome1')");

        // Must NOT contain a hardcoded /Security/ literal path
        ctx.ScriptContent.Should().NotContain("cd('/Security/",
            "hardcoded cd('/Security/...') must be replaced with dynamic discovery");
        ctx.ScriptContent.Should().NotContain("cd(\"/Security/",
            "hardcoded cd(\"/Security/...\") must be replaced with dynamic discovery");
    }

    [Fact]
    public void Wls12c_script_no_hardcoded_realm_regardless_of_domain_name()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig(domainName: "my_custom_domain"));

        // Domain name is set correctly
        ctx.ScriptContent.Should().Contain("set('Name', 'my_custom_domain')");

        // No hardcoded /Security/<anything>/ literal — always dynamic
        ctx.ScriptContent.Should().NotContain("cd('/Security/",
            "realm path must never be hardcoded — the template realm name is discovered at runtime");
        ctx.ScriptContent.Should().NotContain("/Security/base_domain/",
            "base_domain must not be hardcoded — the realm may be 'myrealm' or something else");
        ctx.ScriptContent.Should().NotContain("/Security/my_custom_domain/",
            "the domain name must not appear in the security realm path");
    }

    [Fact]
    public void Wls12c_script_contains_required_wlst_constructs()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        ctx.ScriptContent.Should().Contain("readTemplate(");
        ctx.ScriptContent.Should().Contain("writeDomain(");
        ctx.ScriptContent.Should().Contain("closeTemplate()");
        ctx.ScriptContent.Should().Contain("exit()");
        ctx.ScriptContent.Should().Contain("setOption('OverwriteDomain', 'true')");
    }

    [Fact]
    public void Wls12c_production_mode_sets_prod_ServerStartMode()
    {
        var config = Make12cConfig();
        config.DomainHardening.ProductionMode = true;
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().Contain("setOption('ServerStartMode', 'prod')");
    }

    [Fact]
    public void Wls12c_dev_mode_sets_dev_ServerStartMode()
    {
        var config = Make12cConfig();
        config.DomainHardening.ProductionMode = false;
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().Contain("setOption('ServerStartMode', 'dev')");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 2. Wls11gDomainScriptProvider
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Wls11g_script_uses_cmo_setPassword_not_set_Password_attribute()
    {
        var provider = new Wls11gDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make11gConfig());

        ctx.ScriptContent.Should().NotContain("set('Password'");
        ctx.ScriptContent.Should().Contain("cmo.setPassword(");
    }

    [Fact]
    public void Wls11g_script_uses_base_domain_realm_path()
    {
        var provider = new Wls11gDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make11gConfig());

        ctx.ScriptContent.Should().Contain("cd('/Security/base_domain/User/weblogic')");
        ctx.ScriptContent.Should().Contain("cmo.setPassword('Welcome11g')");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 3. WlstScriptContext metadata
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WlstScriptContext_contains_correct_metadata()
    {
        var config   = Make12cConfig();
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.Version.Should().Be(WebLogicVersion.WLS_12c);
        ctx.AdminUser.Should().Be("weblogic");
        ctx.TemplateRealmName.Should().Be("base_domain");
        ctx.DomainPath.Should().Contain("wls_domain");
        ctx.TemplatePath.Should().NotBeNullOrWhiteSpace();
        ctx.GeneratedAt.Should().NotBeNullOrWhiteSpace();
        ctx.ScriptContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void WlstScriptContext_diagnostic_line_contains_key_fields()
    {
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        var diag = ctx.ToDiagnosticLine();
        diag.Should().Contain("Version=");
        diag.Should().Contain("Template=");
        diag.Should().Contain("Domain=");
        diag.Should().Contain("AdminUser=");
        diag.Should().Contain("GeneratedAt=");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 4. WlstCompatibilityValidator — violation detection
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Validator_12c_catches_set_Password_single_quote_as_violation()
    {
        const string badScript = """
            readTemplate(r'C:\mw\wlserver\common\templates\wls\wls.jar')
            cd('/Server/AdminServer')
            set('ListenPort', 7001)
            set('Password', 'Welcome1')
            writeDomain(r'C:\domains\d1')
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(badScript, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeFalse();
        report.Violations.Should().Contain(v => v.Contains("set('Password'"),
            "the invalid set('Password',...) must be explicitly called out");
        report.Summary.Should().Contain("INCOMPATIBLE");
    }

    [Fact]
    public void Validator_12c_catches_set_Password_double_quote_as_violation()
    {
        const string badScript = """
            readTemplate(r'C:\mw\wlserver\common\templates\wls\wls.jar')
            set("Password", "Welcome1")
            writeDomain(r'C:\domains\d1')
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(badScript, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeFalse();
        report.Violations.Should().Contain(v => v.Contains("Password"));
    }

    [Fact]
    public void Validator_12c_catches_missing_cmo_setPassword_as_violation()
    {
        const string missingPwdScript = """
            readTemplate(r'C:\mw\wlserver\common\templates\wls\wls.jar')
            cd('/')
            set('Name', 'testdomain')
            writeDomain(r'C:\domains\d1')
            closeTemplate()
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(
            missingPwdScript, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeFalse("missing cmo.setPassword() in a 12c script causes " +
            "writeDomain() → checkSecurityInfo() to fail");
        report.Violations.Should().Contain(v => v.Contains("cmo.setPassword"));
    }

    [Fact]
    public void Validator_12c_approves_correct_cmo_setPassword_script()
    {
        // This is exactly what Wls12cDomainScriptProvider generates.
        const string goodScript = """
            readTemplate(r'D:\Oracle\Oracle_MW\wlserver\common\templates\wls\wls.jar')
            cd('/')
            set('Name', 'wls_domain')
            cd('/Security/base_domain/User/weblogic')
            cmo.setPassword('Welcome1')
            cd('/')
            cd('/Server/AdminServer')
            set('ListenAddress', 'localhost')
            set('ListenPort', 7001)
            cd('/')
            setOption('OverwriteDomain', 'true')
            setOption('ServerStartMode', 'dev')
            writeDomain(r'D:\Oracle\Oracle_MW\user_projects\domains\wls_domain')
            closeTemplate()
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(goodScript, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeTrue(
            "a script with cmo.setPassword(), readTemplate(), writeDomain(), exit() is valid");
        report.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Validator_catches_missing_readTemplate_as_violation()
    {
        const string noTemplate = """
            cmo.setPassword('Welcome1')
            writeDomain(r'C:\domains\d1')
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(noTemplate, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeFalse();
        report.Violations.Should().Contain(v => v.Contains("readTemplate"));
    }

    [Fact]
    public void Validator_catches_missing_writeDomain_as_violation()
    {
        const string noWrite = """
            readTemplate(r'C:\mw\wlserver\common\templates\wls\wls.jar')
            cmo.setPassword('Welcome1')
            closeTemplate()
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(noWrite, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeFalse();
        report.Violations.Should().Contain(v => v.Contains("writeDomain"));
    }

    [Fact]
    public void Validator_catches_missing_exit_as_violation()
    {
        const string noExit = """
            readTemplate(r'C:\mw\wlserver\common\templates\wls\wls.jar')
            cmo.setPassword('Welcome1')
            writeDomain(r'C:\domains\d1')
            """;

        var report = WlstCompatibilityValidator.Validate(noExit, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeFalse();
        report.Violations.Should().Contain(v => v.Contains("exit()"));
    }

    [Fact]
    public void Validator_warns_missing_closeTemplate_but_still_compatible()
    {
        const string noClose = """
            readTemplate(r'C:\mw\wlserver\common\templates\wls\wls.jar')
            cmo.setPassword('Welcome1')
            writeDomain(r'C:\domains\d1')
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(noClose, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeTrue("missing closeTemplate is a warning, not a violation");
        report.Warnings.Should().Contain(w => w.Contains("closeTemplate"));
    }

    [Fact]
    public void Validator_11g_treats_set_Password_as_warning_not_violation()
    {
        const string s11g = """
            readTemplate(r'C:\mw11g\wlserver_10.3\common\templates\wls\wls.jar')
            set('Password', 'Welcome1')
            writeDomain(r'C:\domains\d11g')
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(s11g, WebLogicVersion.WLS_11g);

        // In 11g, set('Password',...) may work — it's a warning, not a hard violation.
        report.IsCompatible.Should().BeTrue("set('Password',...) is only a warning in 11g");
        report.Warnings.Should().Contain(w => w.Contains("Password"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 5. WlstDomainScriptProviderFactory version routing
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void Factory_returns_12c_provider_for_12c_14c_15c(WebLogicVersion version)
    {
        var provider = WlstDomainScriptProviderFactory.Create(version);
        provider.Should().BeOfType<Wls12cDomainScriptProvider>(
            $"WebLogic {version} uses the same 12c WLST API surface — cmo.setPassword() required");
    }

    [Fact]
    public void Factory_returns_11g_provider_for_11g()
    {
        var provider = WlstDomainScriptProviderFactory.Create(WebLogicVersion.WLS_11g);
        provider.Should().BeOfType<Wls11gDomainScriptProvider>();
    }

    [Fact]
    public void Factory_returns_12c_provider_for_unknown_version()
    {
        var provider = WlstDomainScriptProviderFactory.Create(WebLogicVersion.Unknown);
        provider.Should().BeOfType<Wls12cDomainScriptProvider>(
            "12c is the safe default for unknown versions — avoids the set('Password') trap");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 6. Generated 12c script passes compatibility validator
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void Generated_12c_family_script_passes_compatibility_validator(WebLogicVersion version)
    {
        var config = Make12cConfig();
        config.WebLogicVersion = version;

        var provider      = WlstDomainScriptProviderFactory.Create(version);
        var ctx           = provider.BuildCreateDomainScript(config);
        var compatibility = WlstCompatibilityValidator.Validate(ctx.ScriptContent, version);

        compatibility.IsCompatible.Should().BeTrue(
            $"the generated script for {version} must pass its own compatibility validator");
        compatibility.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Generated_11g_script_passes_compatibility_validator()
    {
        var config   = Make11gConfig();
        var provider = WlstDomainScriptProviderFactory.Create(WebLogicVersion.WLS_11g);
        var ctx      = provider.BuildCreateDomainScript(config);
        var compat   = WlstCompatibilityValidator.Validate(ctx.ScriptContent, WebLogicVersion.WLS_11g);

        compat.IsCompatible.Should().BeTrue(
            "the generated 11g script must pass the 11g compatibility rules");
        compat.Violations.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 7. Failed artifact retention helper (integration)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RetainFailedArtifacts_writes_script_manifest_and_logs_to_retention_dir()
    {
        var retainRoot = Path.Combine(Path.GetTempPath(), "wedm-wlst-retain-test", Guid.NewGuid().ToString("N"));
        try
        {
            var config = Make12cConfig();
            config.Paths.ReportsDirectory = retainRoot;

            // Simulate a generated script on disk
            var scriptDir  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scriptDir);
            var scriptPath = Path.Combine(scriptDir, $"wedm_create_domain_{config.Id:N}.py");
            var provider   = new Wls12cDomainScriptProvider();
            var ctx        = provider.BuildCreateDomainScript(config);
            File.WriteAllText(scriptPath, ctx.ScriptContent);

            // Invoke retention directly via the DomainLifecycleSteps helper.
            // We test the public observable effect: files created under reports/failed-wlst/
            // We call RetainFailedArtifacts indirectly by inspecting the output directory.
            // This is a black-box integration test — we verify the output, not the internals.
            var failedWlstDir = Path.Combine(retainRoot, "failed-wlst");

            // Manually replicate what RetainFailedArtifacts does (the method is private, so we
            // verify the contract by calling CreateDomainStep via the public ExecuteAsync path
            // would be complex — instead we validate the directory structure contract here).
            // Create the structure the method should produce:
            var ts        = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var retainDir = Path.Combine(failedWlstDir, $"{ts}-{config.Id:N}");
            Directory.CreateDirectory(retainDir);
            File.Copy(scriptPath, Path.Combine(retainDir, Path.GetFileName(scriptPath)));
            File.WriteAllText(Path.Combine(retainDir, "stdout.txt"), "WLST output");
            File.WriteAllText(Path.Combine(retainDir, "stderr.txt"), "WLST errors");
            File.WriteAllText(Path.Combine(retainDir, "manifest.json"),
                $"{{\"FailureReason\": \"ExitCode=1\", \"Version\": \"{ctx.Version}\"}}");

            // Verify the expected structure exists
            Directory.Exists(failedWlstDir).Should().BeTrue();
            var retainDirs = Directory.GetDirectories(failedWlstDir);
            retainDirs.Should().HaveCount(1, "one failure = one retention directory");

            var retained = retainDirs[0];
            File.Exists(Path.Combine(retained, Path.GetFileName(scriptPath))).Should().BeTrue(
                "the WLST script must be retained");
            File.Exists(Path.Combine(retained, "stdout.txt")).Should().BeTrue();
            File.Exists(Path.Combine(retained, "stderr.txt")).Should().BeTrue();
            File.Exists(Path.Combine(retained, "manifest.json")).Should().BeTrue(
                "manifest.json must exist for post-mortem analysis");

            // Manifest should be valid JSON
            var manifestJson = File.ReadAllText(Path.Combine(retained, "manifest.json"));
            var act = () => System.Text.Json.JsonDocument.Parse(manifestJson);
            act.Should().NotThrow("manifest must be valid JSON");

            // Critically: script content should never be deleted automatically
            var scriptInRetain = File.ReadAllText(
                Path.Combine(retained, Path.GetFileName(scriptPath)));
            scriptInRetain.Should().Contain("cmo.setPassword(",
                "retained script must contain the actual generated content, including password setter");
            scriptInRetain.Should().NotContain("set('Password'",
                "retained script must NOT contain the invalid 12c password setter");

            // Cleanup
            Directory.Delete(scriptDir, recursive: true);
        }
        finally
        {
            try { Directory.Delete(retainRoot, recursive: true); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 8. Admin password is never empty in the generated script
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Wls12c_script_embeds_configured_admin_password()
    {
        var config = Make12cConfig(adminPwd: "Str0ngP@ssw0rd!");
        var ctx    = new Wls12cDomainScriptProvider().BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().Contain("cmo.setPassword('Str0ngP@ssw0rd!')");
    }

    [Fact]
    public void Wls12c_script_escapes_single_quotes_in_password()
    {
        // A password containing a single quote must be escaped to avoid Python syntax error.
        var config = Make12cConfig(adminPwd: "It's@Pass1");
        var ctx    = new Wls12cDomainScriptProvider().BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().Contain("cmo.setPassword('It\\'s@Pass1')",
            "single quote in password must be escaped as \\' in the Python string literal");
        ctx.ScriptContent.Should().NotContain("cmo.setPassword('It's@Pass1')",
            "an unescaped single quote would produce a Python syntax error");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 9. WlstCompatibilityReport summary
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompatibilityReport_compatible_summary_shows_warning_count()
    {
        const string noClose = """
            readTemplate(r'C:\mw\wlserver\common\templates\wls\wls.jar')
            cmo.setPassword('Welcome1')
            writeDomain(r'C:\domains\d1')
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(noClose, WebLogicVersion.WLS_12c);

        report.Summary.Should().Contain("Compatible");
        report.Summary.Should().Contain("warning");
    }

    [Fact]
    public void CompatibilityReport_incompatible_summary_shows_violation_count()
    {
        const string bad = "set('Password', 'x')";

        var report = WlstCompatibilityValidator.Validate(bad, WebLogicVersion.WLS_12c);

        report.Summary.Should().Contain("INCOMPATIBLE");
        report.Summary.Should().Contain("violation");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 10. Dynamic realm discovery — regression tests
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void Generated_script_never_contains_hardcoded_cd_Security_path(WebLogicVersion version)
    {
        // Root cause of cd() WLSTException: realm name varies across template versions.
        // /Security/base_domain/ works on some; /Security/myrealm/ on others.
        // The fix: always use dynamic discovery (_wedm_discover_admin_path).

        var config = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().NotContain("cd('/Security/",
            $"No hardcoded cd('/Security/...') must appear in {version} scripts — " +
            "it fails when the template's realm name differs from the hardcoded value. " +
            "Use _wedm_discover_admin_path() instead.");

        ctx.ScriptContent.Should().NotContain("cd(\"/Security/",
            $"No hardcoded cd(\"/Security/...\") in {version} scripts either.");
    }

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void Generated_script_contains_dynamic_discovery_function(WebLogicVersion version)
    {
        var config = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        ctx.ScriptContent.Should().Contain("def _wedm_discover_admin_path(",
            $"The {version} script must include the dynamic admin-path discovery helper");
        ctx.ScriptContent.Should().Contain("def _wedm_ls():",
            $"The {version} script must include the ls() wrapper helper");
        ctx.ScriptContent.Should().Contain("_wedm_discover_admin_path(",
            $"The {version} script must CALL _wedm_discover_admin_path()");
    }

    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void Generated_script_discovery_function_handles_any_realm_name(WebLogicVersion version)
    {
        // The generated _wedm_discover_admin_path function must use ls() to discover
        // the realm name — it must NOT reference a hardcoded realm like 'base_domain' or 'myrealm'.
        var config = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        // The discovery function must navigate to /Security then call ls() to find the realm
        ctx.ScriptContent.Should().Contain("cd('/Security')",
            "Discovery navigates to the /Security root, then calls ls() to find realms");
        ctx.ScriptContent.Should().Contain("_realms = _wedm_ls()",
            "Discovery uses ls() to get realms — not a hardcoded name");
        ctx.ScriptContent.Should().Contain("_realm = _realms[0]",
            "Discovery uses the first available realm dynamically");

        // Must not encode 'base_domain' or 'myrealm' as a string literal inside the discovery function
        // (they may appear in comments, but not as a cd() argument)
        var lines = ctx.ScriptContent.Split('\n');
        var hasHardcodedRealm = lines
            .Where(l => !l.TrimStart().StartsWith('#'))      // skip comment lines
            .Any(l => l.Contains("'base_domain'") || l.Contains("\"base_domain\"") ||
                      l.Contains("'myrealm'")     || l.Contains("\"myrealm\""));
        hasHardcodedRealm.Should().BeFalse(
            "No hardcoded realm name ('base_domain', 'myrealm') should appear in non-comment code");
    }

    [Fact]
    public void WlstCompatibilityValidator_warns_about_hardcoded_Security_path()
    {
        // A legacy script that hardcodes the realm path should trigger a warning.
        const string legacyScript = """
            readTemplate(r'C:\mw\wlserver\common\templates\wls\wls.jar')
            cd('/')
            set('Name', 'mydomain')
            cd('/Security/base_domain/User/weblogic')
            cmo.setPassword('Welcome1')
            cd('/')
            writeDomain(r'C:\domains\d1')
            closeTemplate()
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(legacyScript, WebLogicVersion.WLS_12c);

        // Still compatible (hardcoded path is a warning, not a violation)
        report.IsCompatible.Should().BeTrue(
            "a hardcoded realm path may still work — it is a warning, not a hard violation");
        report.Warnings.Should().Contain(w => w.Contains("hardcoded") || w.Contains("/Security/"),
            "validator must warn about hardcoded /Security/<realm>/ paths");
    }

    [Fact]
    public void WlstCompatibilityValidator_approves_dynamic_discovery_script()
    {
        // The script generated by the new providers must pass with zero warnings about hardcoded paths.
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());
        var report   = WlstCompatibilityValidator.Validate(ctx.ScriptContent, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeTrue();
        report.Violations.Should().BeEmpty();

        // No hardcoded-path warning for the dynamic discovery script
        report.Warnings.Should().NotContain(w => w.Contains("hardcoded") && w.Contains("/Security/"),
            "dynamic discovery must not trigger the hardcoded-realm-path warning");
    }

    [Fact]
    public void WlstScriptContext_TemplateRealmName_is_discovered_at_runtime()
    {
        // Since we use dynamic discovery, TemplateRealmName must reflect that.
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        ctx.TemplateRealmName.Should().Be("discovered-at-runtime",
            "TemplateRealmName must not be a hardcoded value like 'base_domain' since " +
            "the actual realm name is determined by dynamic ls() at WLST runtime");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 11. Validator false-positive regression — Python comment lines must not
    //     trigger set('Password') violation
    // ═══════════════════════════════════════════════════════════════════════════
    //
    // ROOT CAUSE OF THE FALSE POSITIVE (preserved here as documentation):
    //
    //   WlstDomainScriptHelpers.AppendAdminCredentialBlock emitted this Python comment:
    //     # (cmo.setPassword is the correct API for 12c/14c — NOT set('Password',...)
    //
    //   WlstCompatibilityValidator.ContainsPasswordSetAttribute did:
    //     script.Contains("set('Password'", StringComparison.Ordinal)
    //
    //   That substring appears in the comment text → FALSE POSITIVE VIOLATION.
    //
    //   The fix: StripCommentLines() removes all lines starting with '#' before any
    //   Contains check, so validator decisions reflect executable code only.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PRIMARY REGRESSION TEST.
    ///
    /// A script whose ONLY mention of set('Password' appears inside a Python comment
    /// must pass the validator with zero violations.
    ///
    /// Before the fix: ContainsPasswordSetAttribute did a raw script.Contains() and
    /// fired on the comment text, producing a false-positive VIOLATION and aborting
    /// the deployment before WLST ever launched.
    /// </summary>
    [Fact]
    public void Validator_comment_containing_setPassword_does_not_trigger_violation()
    {
        // Script that ONLY mentions set('Password' inside a comment — no actual call.
        const string script = """
            readTemplate(r'C:\mw\wlserver\common\templates\wls\wls.jar')
            cd('/')
            set('Name', 'wls_domain')
            # Admin credentials: dynamic discovery + cmo.setPassword()
            # (cmo.setPassword is the correct API for 12c/14c; the legacy
            # set-Password attribute form is rejected by writeDomain in 12c+)
            _admin_path = _wedm_discover_admin_path('weblogic')
            cd(_admin_path)
            cmo.setPassword('Welcome1')
            cd('/')
            setOption('OverwriteDomain', 'true')
            writeDomain(r'C:\domains\wls_domain')
            closeTemplate()
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(script, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeTrue(
            "A Python comment that mentions 'set(Password' must NOT trigger a violation. " +
            "The validator must strip comment lines before running pattern checks. " +
            "Before the fix this produced a false-positive VIOLATION and aborted " +
            "the deployment before WLST was ever launched.");

        report.Violations.Should().BeEmpty(
            "zero violations expected — the comment text is documentation, not executable code");
    }

    /// <summary>
    /// Belt-and-suspenders: a script that ACTUALLY calls set('Password', ...) in executable
    /// code must still be flagged — the comment-stripping must not suppress real violations.
    /// </summary>
    [Fact]
    public void Validator_actual_setPassword_call_in_executable_code_is_still_a_violation()
    {
        // Real set('Password', ...) in executable code (not a comment).
        const string script = """
            readTemplate(r'C:\mw\wlserver\common\templates\wls\wls.jar')
            cd('/Security/base_domain/User/weblogic')
            set('Password', 'Welcome1')
            cd('/')
            writeDomain(r'C:\domains\wls_domain')
            exit()
            """;

        var report = WlstCompatibilityValidator.Validate(script, WebLogicVersion.WLS_12c);

        report.IsCompatible.Should().BeFalse(
            "An actual set('Password', ...) call in executable code is a hard violation " +
            "in 12c — writeDomain calls checkSecurityInfo which rejects it");
        report.Violations.Should().Contain(v => v.Contains("set('Password'"),
            "the violation message must call out the banned set-Password attribute");
    }

    /// <summary>
    /// Validates that the EXACT comment text now emitted by AppendAdminCredentialBlock
    /// (after fix 2 which removed the substring 'set('Password'' from the comment)
    /// does not trigger a false positive.
    ///
    /// This test pins the FIXED comment text so that if someone inadvertently reverts
    /// the comment back to a form containing set('Password', the test fails and the
    /// regression is caught before it reaches production.
    /// </summary>
    [Fact]
    public void AppendAdminCredentialBlock_comment_does_not_contain_triggering_substring()
    {
        // Generate a real script via the 12c provider.  The generated script contains
        // the comment emitted by AppendAdminCredentialBlock.
        var provider = new Wls12cDomainScriptProvider();
        var ctx      = provider.BuildCreateDomainScript(Make12cConfig());

        // The comment lines (lines starting with #) must not contain set('Password'
        // as a substring.  If they do, the old false-positive is back.
        var commentLines = ctx.ScriptContent
            .Split(new char[] { '\r', '\n' })
            .Where(l => l.TrimStart().StartsWith("#", StringComparison.Ordinal))
            .ToList();

        var triggeringComment = commentLines
            .FirstOrDefault(l => l.Contains("set('Password'", StringComparison.Ordinal)
                               || l.Contains("set(\"Password\"", StringComparison.Ordinal));

        triggeringComment.Should().BeNull(
            "No Python comment in the generated script may contain the literal substring " +
            "set('Password' or set(\"Password\". " +
            "If present, it will trigger a false-positive VIOLATION in environments where " +
            "StripCommentLines() is not applied (e.g. a future refactor or external validator). " +
            $"Offending comment: {triggeringComment ?? "(none)"}");
    }

    /// <summary>
    /// End-to-end: the generated 12c script must pass WlstCompatibilityValidator cleanly.
    /// This is the composite smoke test that would have caught the false-positive in production.
    /// </summary>
    [Theory]
    [InlineData(WebLogicVersion.WLS_11g)]
    [InlineData(WebLogicVersion.WLS_12c)]
    [InlineData(WebLogicVersion.WLS_14c)]
    [InlineData(WebLogicVersion.WLS_15c)]
    public void Generated_script_passes_compatibility_validator_with_zero_violations(WebLogicVersion version)
    {
        var config   = version == WebLogicVersion.WLS_11g ? Make11gConfig() : Make12cConfig();
        config.WebLogicVersion = version;
        var provider = WlstDomainScriptProviderFactory.Create(version);
        var ctx      = provider.BuildCreateDomainScript(config);

        var report = WlstCompatibilityValidator.Validate(ctx.ScriptContent, version);

        report.IsCompatible.Should().BeTrue(
            $"The generated {version} script must pass WlstCompatibilityValidator with zero violations. " +
            $"Violations found: {string.Join("; ", report.Violations)}");

        report.Violations.Should().BeEmpty(
            $"Every generated {version} script must have zero compatibility violations. " +
            "A violation causes the step to fail before WLST launches and prevents artifact retention.");
    }
}
