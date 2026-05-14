using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WEDM.Domain.Interfaces;
using WEDM.Domain.Models;
using WEDM.Engine.Workflow;

namespace WEDM.UI.Services;

public sealed class AppProductInfoProvider : IProductInfoProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ProductVersionSnapshot GetSnapshot()
    {
        var ui = Assembly.GetEntryAssembly() ?? typeof(App).Assembly;
        var engine = typeof(DeploymentWorkflowEngine).Assembly;

        var uiFileVer = TryGetFileVersion(ui);
        var engFileVer = TryGetFileVersion(engine);
        var infoVer = ui.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? ui.GetName().Version?.ToString() ?? "?";

        WedmProductSidecar? sidecar = null;
        var sidecarPath = Path.Combine(AppContext.BaseDirectory, "wedm-product.json");
        if (File.Exists(sidecarPath))
        {
            try
            {
                var json = File.ReadAllText(sidecarPath);
                sidecar = JsonSerializer.Deserialize<WedmProductSidecar>(json, JsonOptions);
            }
            catch
            {
                /* non-fatal — fall back to assembly metadata */
            }
        }

        var productName = string.IsNullOrWhiteSpace(sidecar?.ProductName)
            ? ui.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "WebLogic Enterprise Deployment Manager"
            : sidecar!.ProductName!;

        var channel = string.IsNullOrWhiteSpace(sidecar?.ReleaseChannel)
            ? "Stable"
            : sidecar.ReleaseChannel!;

        string? notesPath = null;
        if (!string.IsNullOrWhiteSpace(sidecar?.ReleaseNotesRelativePath))
            notesPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, sidecar.ReleaseNotesRelativePath));

        string? updatePath = null;
        if (!string.IsNullOrWhiteSpace(sidecar?.UpdateFeedRelativePath))
            updatePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, sidecar.UpdateFeedRelativePath));

        return new ProductVersionSnapshot(
            ProductName: productName,
            DisplayVersion: infoVer,
            InformationalVersion: infoVer,
            UiAssemblyFileVersion: uiFileVer,
            EngineAssemblyFileVersion: engFileVer,
            ReleaseChannel: channel,
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            ReleaseNotesPath: notesPath,
            UpdateManifestPath: updatePath);
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000:Assembly.Location returns empty for single-file bundles",
        Justification = "File version is optional; AssemblyName.Version is used when Location is unavailable.")]
    private static string TryGetFileVersion(Assembly assembly)
    {
        var loc = assembly.Location;
        if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(loc).FileVersion
                       ?? assembly.GetName().Version?.ToString() ?? "?";
            }
            catch
            {
                /* single-file or restricted host */
            }
        }

        return assembly.GetName().Version?.ToString() ?? "?";
    }
}
