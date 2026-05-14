using System.Text.RegularExpressions;
using WEDM.Domain.Models;

namespace WEDM.Engine.Opatch;

/// <summary>Parses <c>opatch lsinventory</c> text output into structured patch records.</summary>
public static class OpatchInventoryParser
{
    private static readonly Regex PatchLine = new(
        @"Patch\s+(\d+)\s*:\s*(?:applied on|Installed)\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<AppliedPatchRecord> Parse(string rawText)
    {
        var list = new List<AppliedPatchRecord>();
        if (string.IsNullOrWhiteSpace(rawText)) return list;

        foreach (var line in rawText.Split('\n'))
        {
            var m = PatchLine.Match(line.Trim());
            if (!m.Success) continue;
            list.Add(new AppliedPatchRecord
            {
                PatchId     = m.Groups[1].Value.Trim(),
                AppliedOn   = m.Groups[2].Value.Trim(),
                Description = null
            });
        }

        return list;
    }
}
