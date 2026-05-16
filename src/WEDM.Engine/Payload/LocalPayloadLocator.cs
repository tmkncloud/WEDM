using System.Security.Cryptography;
using WEDM.Domain.Enums;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Versioning.PayloadLayouts;

namespace WEDM.Engine.Payload;

public sealed class LocalPayloadLocator : IPayloadLocator
{
    private readonly ILoggingService _log;
    private readonly LocalPayloadManifestReader _manifests = new();

    public LocalPayloadLocator(ILoggingService log) => _log = log;

    public string GetRepositoryRoot(DeploymentConfiguration config)
        => Path.GetFullPath(config.PayloadBasePath);

    public string GetVersionFolderPath(DeploymentConfiguration config)
    {
        var layout = LocalPayloadLayoutProvider.For(config);
        return Path.Combine(GetRepositoryRoot(config), layout.RepositoryFolderName);
    }

    public async Task<LocalPayloadRepositoryReport> ValidateAndResolveAsync(
        DeploymentConfiguration config,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var layout       = LocalPayloadLayoutProvider.For(config);
        var versionRoot  = GetVersionFolderPath(config);
        var definitions  = layout.GetDefinitions(config);
        var manifest     = _manifests.TryLoad(versionRoot);
        var report       = new LocalPayloadRepositoryReport
        {
            RepositoryRoot   = GetRepositoryRoot(config),
            VersionFolder    = versionRoot,
            ManifestPresent  = manifest is not null
        };

        if (!Directory.Exists(versionRoot))
        {
            report.Findings.Add(Fatal(
                "LocalPayload.VersionRoot",
                $"Local payload version folder not found: {versionRoot}",
                $"Create the folder and add subdirectories: {string.Join(", ", definitions.Select(d => d.FolderName).Distinct())}.",
                versionRoot,
                null));
            report.CanProceed = false;
            return report;
        }

        _log.Info($"Local payload repository: {versionRoot} ({definitions.Count} slot(s))", "Payload.Local");

        foreach (var def in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderPath = Path.Combine(versionRoot, def.FolderName);
            var entry      = ResolveDefinition(def, folderPath, manifest, config);
            report.Entries.Add(entry);

            if (!entry.Found && def.Required)
            {
                report.Findings.Add(Fatal(
                    $"LocalPayload.{def.Component}",
                    entry.Message,
                    BuildRemediation(def, folderPath),
                    folderPath,
                    FormatPatterns(def.FilePatterns)));
            }
            else if (entry.ChecksumStatus == PayloadChecksumStatus.Mismatch)
            {
                report.Findings.Add(Fatal(
                    $"LocalPayload.Checksum.{def.Component}",
                    entry.Message,
                    "Replace the file with the correct Oracle media or update payload-manifest.json.",
                    entry.ResolvedPath,
                    entry.ExpectedSha256));
            }
            else if (entry.ChecksumStatus == PayloadChecksumStatus.ManifestMissing && def.Required && entry.Found)
            {
                report.Findings.Add(Warn(
                    $"LocalPayload.Checksum.{def.Component}",
                    $"No SHA-256 manifest entry for '{def.Component}' — checksum not verified.",
                    $"Add an entry under payloads.{MapManifestKey(def.Component)} in {LocalPayloadManifestReader.ManifestFileName}.",
                    entry.ResolvedPath));
            }
        }

        report.CanProceed = !report.Findings.Any(f => f.Severity == ValidationSeverity.Fatal);
        ApplyResolvedPathsToConfiguration(config, report);
        return report;
    }

    public PayloadResolutionResult Resolve(LocalPayloadComponent component, DeploymentConfiguration config)
    {
        var entry = config.LocalPayload.Entries.FirstOrDefault(e => e.Component == component);
        if (entry is null)
            return new() { Status = PayloadResolutionStatus.Failed, Message = $"Component '{component}' was not resolved." };

        if (!entry.Found)
            return new() { Status = PayloadResolutionStatus.Failed, Message = entry.Message, Component = component };

        return new()
        {
            Status          = PayloadResolutionStatus.ResolvedExisting,
            InstallerPath   = entry.ResolvedPath,
            Message         = entry.Message,
            Component       = component,
            RepositoryFolder = config.LocalPayload.VersionFolder,
            ChecksumStatus  = entry.ChecksumStatus,
            ExpectedSha256  = entry.ExpectedSha256,
            ActualSha256    = entry.ActualSha256
        };
    }

    public void ApplyResolvedPathsToConfiguration(DeploymentConfiguration config, LocalPayloadRepositoryReport report)
    {
        config.LocalPayload = new LocalPayloadResolutionSnapshot
        {
            UsedLocalRepository = true,
            RepositoryRoot      = report.RepositoryRoot,
            VersionFolder       = report.VersionFolder,
            ManifestPresent     = report.ManifestPresent,
            Entries             = report.Entries.ToList()
        };

        foreach (var entry in report.Entries.Where(e => e.Found && !string.IsNullOrWhiteSpace(e.ResolvedPath)))
        {
            switch (entry.Component)
            {
                case LocalPayloadComponent.Jdk:
                    config.JdkInstallerPath = entry.ResolvedPath!;
                    break;
                case LocalPayloadComponent.Vc:
                    if (entry.ResolvedPath!.Contains("x86", StringComparison.OrdinalIgnoreCase)
                        && !entry.ResolvedPath.Contains("x64", StringComparison.OrdinalIgnoreCase))
                        config.VcRedistX86InstallerPath = entry.ResolvedPath;
                    else
                        config.VcRedistX64InstallerPath = entry.ResolvedPath;
                    break;
                case LocalPayloadComponent.Infrastructure:
                    config.InfrastructureInstallerPath = entry.ResolvedPath!;
                    break;
                case LocalPayloadComponent.WebLogic:
                    config.WebLogicInstallerPath = entry.ResolvedPath!;
                    break;
                case LocalPayloadComponent.Forms:
                    config.FormsInstallerPath = entry.ResolvedPath!;
                    break;
                case LocalPayloadComponent.WebTier:
                    config.WebTierInstallerPath = entry.ResolvedPath!;
                    break;
                case LocalPayloadComponent.WebUtil:
                    config.WebUtilRootPath = entry.ResolvedPath!;
                    break;
            }
        }
    }

    private ResolvedLocalPayloadEntry ResolveDefinition(
        LocalPayloadFolderDefinition def,
        string folderPath,
        PayloadManifestDocument? manifest,
        DeploymentConfiguration config)
    {
        if (!Directory.Exists(folderPath))
        {
            return new ResolvedLocalPayloadEntry
            {
                Component    = def.Component,
                FolderPath   = folderPath,
                Required     = def.Required,
                Found        = false,
                Message      = $"Missing payload folder: {folderPath}"
            };
        }

        if (def.DirectoryPayload)
            return ResolveDirectoryPayload(def, folderPath);

        var match = LocalPayloadPatternMatcher.FindBestMatch(folderPath, def.FilePatterns);
        if (match is null)
        {
            return new ResolvedLocalPayloadEntry
            {
                Component    = def.Component,
                FolderPath   = folderPath,
                Required     = def.Required,
                Found        = false,
                MatchedPatterns = def.FilePatterns,
                Message      = $"No file matching [{string.Join(", ", def.FilePatterns)}] in {folderPath}"
            };
        }

        return BuildFileEntry(def, folderPath, match, manifest, config);
    }

    private static ResolvedLocalPayloadEntry ResolveDirectoryPayload(LocalPayloadFolderDefinition def, string folderPath)
    {
        var missing = def.RequiredSubfolders
            .Where(sf => !Directory.Exists(Path.Combine(folderPath, sf)))
            .ToList();

        if (missing.Count > 0)
        {
            return new ResolvedLocalPayloadEntry
            {
                Component  = def.Component,
                FolderPath = folderPath,
                Required   = def.Required,
                Found      = false,
                Message    = $"WebUtil folder incomplete — missing: {string.Join(", ", missing)} under {folderPath}"
            };
        }

        return new ResolvedLocalPayloadEntry
        {
            Component    = def.Component,
            FolderPath   = folderPath,
            ResolvedPath = folderPath,
            Found        = true,
            Required     = def.Required,
            ChecksumStatus = PayloadChecksumStatus.NotChecked,
            Message      = $"WebUtil payload directory validated: {folderPath}"
        };
    }

    private ResolvedLocalPayloadEntry BuildFileEntry(
        LocalPayloadFolderDefinition def,
        string folderPath,
        string filePath,
        PayloadManifestDocument? manifest,
        DeploymentConfiguration config)
    {
        var entry = new ResolvedLocalPayloadEntry
        {
            Component       = def.Component,
            FolderPath      = folderPath,
            ResolvedPath    = filePath,
            Found           = true,
            Required        = def.Required,
            MatchedPatterns = def.FilePatterns,
            Message         = $"Resolved {def.Component}: {filePath}"
        };

        if (!config.PayloadAcquisition.ValidateChecksums || manifest is null)
        {
            entry.ChecksumStatus = manifest is null
                ? PayloadChecksumStatus.ManifestMissing
                : PayloadChecksumStatus.NotChecked;
            return entry;
        }

        var key = MapManifestKey(def.Component);
        if (!manifest.Payloads.TryGetValue(key, out var manifestEntry))
        {
            entry.ChecksumStatus = PayloadChecksumStatus.ManifestMissing;
            return entry;
        }

        if (string.IsNullOrWhiteSpace(manifestEntry.Sha256))
        {
            entry.ChecksumStatus = PayloadChecksumStatus.WarningSkipped;
            return entry;
        }

        if (!LocalPayloadPatternMatcher.IsMatch(Path.GetFileName(filePath), manifestEntry.File)
            && !string.Equals(Path.GetFileName(filePath), manifestEntry.File, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning(
                $"Manifest file name '{manifestEntry.File}' does not match resolved '{Path.GetFileName(filePath)}' for {def.Component}.",
                "Payload.Local");
        }

        var actual = ComputeSha256(filePath);
        entry.ExpectedSha256 = manifestEntry.Sha256.Trim().ToLowerInvariant();
        entry.ActualSha256   = actual;
        entry.ChecksumStatus = string.Equals(entry.ExpectedSha256, actual, StringComparison.OrdinalIgnoreCase)
            ? PayloadChecksumStatus.Verified
            : PayloadChecksumStatus.Mismatch;

        if (entry.ChecksumStatus == PayloadChecksumStatus.Mismatch)
            entry.Message = $"Checksum mismatch for {Path.GetFileName(filePath)} (expected {entry.ExpectedSha256}, actual {actual}).";

        return entry;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string MapManifestKey(LocalPayloadComponent component) => component switch
    {
        LocalPayloadComponent.Jdk            => "jdk",
        LocalPayloadComponent.Vc             => "vc",
        LocalPayloadComponent.Infrastructure => "infrastructure",
        LocalPayloadComponent.WebLogic       => "weblogic",
        LocalPayloadComponent.Forms          => "forms",
        LocalPayloadComponent.WebTier        => "webtier",
        LocalPayloadComponent.WebUtil        => "webutil",
        _ => component.ToString().ToLowerInvariant()
    };

    private static string BuildRemediation(LocalPayloadFolderDefinition def, string folderPath)
        => def.DirectoryPayload
            ? $"Ensure subfolders [{string.Join(", ", def.RequiredSubfolders)}] exist under:\n{folderPath}"
            : $"Place Oracle installer matching [{string.Join(", ", def.FilePatterns)}] inside:\n{folderPath}";

    private static string FormatPatterns(IReadOnlyList<string> patterns)
        => patterns.Count > 0 ? string.Join(", ", patterns) : "(any)";

    private static LocalPayloadValidationFinding Fatal(string code, string msg, string remediation, string? path, string? patterns)
        => new() { Code = code, Severity = ValidationSeverity.Fatal, Message = msg, Remediation = remediation, MissingPath = path, ExpectedPatterns = patterns };

    private static LocalPayloadValidationFinding Warn(string code, string msg, string remediation, string? path)
        => new() { Code = code, Severity = ValidationSeverity.Warning, Message = msg, Remediation = remediation, MissingPath = path };
}
