using System.Xml.Linq;
using MuClient.Core.Automation;

namespace MuClient.Core.Configuration;

/// <summary>
/// Best-effort importer for BeipMU's XML settings. BeipMU's schema is not formally documented
/// here, so this parser is deliberately tolerant: it scans for world-like elements and reads
/// name/host/port plus any nested triggers/aliases by common element and attribute names,
/// case-insensitively. Unrecognised data is ignored rather than failing the import.
/// </summary>
public static class BeipMuImporter
{
    public static IReadOnlyList<WorldDefinition> Import(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<WorldDefinition>();
        }

        var worlds = new List<WorldDefinition>();
        foreach (var element in doc.Descendants().Where(e => LocalNameContains(e, "world")))
        {
            var world = TryReadWorld(element);
            if (world is not null)
            {
                worlds.Add(world);
            }
        }

        return worlds;
    }

    public static IReadOnlyList<WorldDefinition> ImportFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Import(File.ReadAllText(path));
    }

    private static WorldDefinition? TryReadWorld(XElement element)
    {
        var host = Value(element, "host") ?? Value(element, "address") ?? Value(element, "server");
        var name = Value(element, "name") ?? Value(element, "title") ?? host;
        var portText = Value(element, "port");

        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var world = new WorldDefinition
        {
            Name = name ?? host,
            Host = host,
            Port = int.TryParse(portText, out var port) ? port : 4000,
            UseTls = ParseBool(Value(element, "ssl") ?? Value(element, "tls") ?? Value(element, "secure")),
        };

        foreach (var t in element.Descendants().Where(e => LocalNameContains(e, "trigger")))
        {
            var pattern = Value(t, "pattern") ?? Value(t, "match") ?? Value(t, "regex");
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var send = Value(t, "send") ?? Value(t, "command") ?? Value(t, "response");
            world.Triggers.Add(new Trigger
            {
                Name = Value(t, "name") ?? string.Empty,
                Pattern = pattern,
                Actions = new TriggerActions
                {
                    Gag = ParseBool(Value(t, "gag") ?? Value(t, "omit")),
                    SendResponse = string.IsNullOrWhiteSpace(send) ? null : send,
                },
            });
        }

        foreach (var a in element.Descendants().Where(e => LocalNameContains(e, "alias")))
        {
            var pattern = Value(a, "pattern") ?? Value(a, "match") ?? Value(a, "name");
            var substitution = Value(a, "send") ?? Value(a, "command") ?? Value(a, "expand");
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(substitution))
            {
                continue;
            }

            world.Aliases.Add(new Alias
            {
                Name = Value(a, "name") ?? string.Empty,
                Pattern = pattern,
                Substitution = substitution,
            });
        }

        return world;
    }

    private static bool LocalNameContains(XElement element, string fragment) =>
        element.Name.LocalName.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a value from either a same-named attribute or a same-named child element.</summary>
    private static string? Value(XElement element, string name)
    {
        var attribute = element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (attribute is not null && !string.IsNullOrWhiteSpace(attribute.Value))
        {
            return attribute.Value;
        }

        var child = element.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return child is not null && !string.IsNullOrWhiteSpace(child.Value) ? child.Value : null;
    }

    private static bool ParseBool(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value == "1" ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
