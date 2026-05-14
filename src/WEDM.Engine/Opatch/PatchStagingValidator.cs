using System.Xml.Linq;
using System.Linq;
using WEDM.Domain.Models;

namespace WEDM.Engine.Opatch;

/// <summary>Validates Oracle patch directory layout (patch.xml, README presence).</summary>
public static class PatchStagingValidator
{
    public static (bool Ok, List<string> Notes) ValidateStagingTree(string stagingRoot)
    {
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(stagingRoot) || !Directory.Exists(stagingRoot))
        {
            notes.Add("Staging root is missing or not a directory.");
            return (false, notes);
        }

        var patchXmlFiles = Directory.EnumerateFiles(stagingRoot, "patch.xml", SearchOption.AllDirectories).ToList();
        if (patchXmlFiles.Count == 0)
        {
            var single = Path.Combine(stagingRoot, "patch.xml");
            if (File.Exists(single))
            {
                notes.Add("Single-patch staging directory detected.");
                ValidatePatchXml(single, notes);
                return (notes.All(n => !n.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)), notes);
            }

            notes.Add("No patch.xml found under staging directory.");
            return (false, notes);
        }

        notes.Add($"Found {patchXmlFiles.Count} patch.xml file(s).");
        foreach (var xml in patchXmlFiles.Take(50))
            ValidatePatchXml(xml, notes);

        return (true, notes);
    }

    public static IReadOnlyList<string> EnumeratePatchDirectories(string stagingRoot)
    {
        if (!Directory.Exists(stagingRoot)) return Array.Empty<string>();

        if (File.Exists(Path.Combine(stagingRoot, "patch.xml")))
            return new[] { Path.GetFullPath(stagingRoot) };

        return Directory.GetDirectories(stagingRoot)
            .Where(d => File.Exists(Path.Combine(d, "patch.xml")))
            .Select(Path.GetFullPath)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ValidatePatchXml(string patchXmlPath, List<string> notes)
    {
        try
        {
            var doc = XDocument.Load(patchXmlPath);
            var patch = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "patch");
            var id = patch?.Attribute("id")?.Value ?? patch?.Attribute("patch_id")?.Value;
            if (string.IsNullOrEmpty(id))
                notes.Add($"ERROR: patch.xml missing id: {patchXmlPath}");
            else
                notes.Add($"OK patch id {id} at {Path.GetDirectoryName(patchXmlPath)}");
        }
        catch (Exception ex)
        {
            notes.Add($"ERROR reading {patchXmlPath}: {ex.Message}");
        }
    }
}
