using System.Xml.Linq;

namespace WEDM.Engine.Discovery.Parsers;

/// <summary>Shared WebLogic domain config.xml element access (child elements, not attributes).</summary>
public static class WebLogicConfigXmlHelper
{
    private static readonly XNamespace DomainNs = "http://xmlns.oracle.com/weblogic/domain";

    public static XElement? FindServerByName(XDocument doc, string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return null;
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "server")
            .FirstOrDefault(s => string.Equals(ReadChildValue(s, "name"), serverName, StringComparison.Ordinal));
    }

    public static string? ReadChildValue(XElement? parent, string localName)
    {
        if (parent is null) return null;
        return (parent.Element(DomainNs + localName) ?? parent.Element(localName))?.Value?.Trim();
    }

    public static int? ReadChildInt(XElement? parent, string localName)
    {
        var v = ReadChildValue(parent, localName);
        return int.TryParse(v, out var n) ? n : null;
    }
}
