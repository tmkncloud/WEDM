using System.Xml.Linq;
using WEDM.Domain.Models;

namespace WEDM.Engine.Discovery.Parsers;

/// <summary>XML-based SSL enablement detection for WebLogic domain config.xml.</summary>
public static class WebLogicSslDetector
{
    private static readonly XNamespace DomainNs = "http://xmlns.oracle.com/weblogic/domain";

    public static SslDetectionResult Analyze(string domainHome, string? configXmlPath = null)
    {
        var result = new SslDetectionResult();
        configXmlPath ??= Path.Combine(domainHome, "config", "config.xml");
        if (!File.Exists(configXmlPath))
        {
            result.Warnings.Add($"config.xml not found: {configXmlPath}");
            return result;
        }

        try
        {
            var doc = XDocument.Load(configXmlPath, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            if (root is null)
            {
                result.Warnings.Add("config.xml has no root element.");
                return result;
            }

            var adminName = ReadElement(root, "admin-server-name") ?? "AdminServer";
            foreach (var server in Servers(root))
            {
                var name = ReadElement(server, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var ssl = server.Element(DomainNs + "ssl") ?? server.Element("ssl");
                var listenPort = ReadElement(server, "listen-port");
                var sslListenPort = ReadElement(ssl, "listen-port");
                var enabled = IsSslElementEnabled(ssl);

                if (name.Equals(adminName, StringComparison.OrdinalIgnoreCase))
                {
                    result.AdminSslEnabled = enabled;
                    if (enabled)
                        result.Details.Add($"AdminServer '{name}' SSL listen port: {sslListenPort ?? listenPort ?? "default"}");
                }
                else if (enabled)
                {
                    result.ManagedServerSslEnabled = true;
                    result.Details.Add($"Managed server '{name}' has SSL enabled.");
                }
            }

            foreach (var channel in root.Elements(DomainNs + "network-access-point").Concat(root.Elements("network-access-point")))
            {
                var protocol = ReadElement(channel, "protocol");
                if (string.IsNullOrEmpty(protocol) ||
                    !protocol.Equals("ssl", StringComparison.OrdinalIgnoreCase) &&
                    !protocol.Equals("t3s", StringComparison.OrdinalIgnoreCase) &&
                    !protocol.Equals("https", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsEnabled(ReadElement(channel, "enabled")))
                {
                    result.ChannelSslEnabled = true;
                    result.Details.Add($"Channel SSL protocol '{protocol}' is enabled on server '{ReadElement(channel, "server-name")}'.");
                }
            }

            result.AnySslEnabled = result.AdminSslEnabled || result.ManagedServerSslEnabled || result.ChannelSslEnabled;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"SSL XML analysis failed: {ex.Message}");
        }

        return result;
    }

    private static IEnumerable<XElement> Servers(XElement root)
        => root.Elements(DomainNs + "server").Concat(root.Elements("server"));

    private static bool IsSslElementEnabled(XElement? sslElement)
    {
        if (sslElement is null) return false;
        var enabledAttr = sslElement.Attribute("enabled")?.Value ?? sslElement.Attribute(XName.Get("enabled"))?.Value;
        if (!string.IsNullOrWhiteSpace(enabledAttr))
            return IsEnabled(enabledAttr);
        return sslElement.Elements().Any();
    }

    private static bool IsEnabled(string? value)
        => bool.TryParse(value, out var b) && b;

    private static string? ReadElement(XElement? parent, string localName)
    {
        if (parent is null) return null;
        return (parent.Element(DomainNs + localName) ?? parent.Element(localName))?.Value?.Trim();
    }
}

public sealed class SslDetectionResult
{
    public bool AdminSslEnabled { get; set; }
    public bool ManagedServerSslEnabled { get; set; }
    public bool ChannelSslEnabled { get; set; }
    public bool AnySslEnabled { get; set; }
    public List<string> Details { get; } = [];
    public List<string> Warnings { get; } = [];

    public string Summary => AnySslEnabled
        ? string.Join("; ", Details.Take(3))
        : "No active SSL listeners detected in domain configuration.";
}
