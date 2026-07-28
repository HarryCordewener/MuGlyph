using System.Text;
using System.Text.Json;

namespace MuClient.Tui;

/// <summary>
/// Accumulates GMCP character stats (e.g. <c>Char.Vitals</c>, <c>Char.Stats</c>) into a flat
/// key/value model and renders a compact one-line summary for the stat pane. Deliberately
/// generic: any flat JSON object's scalar fields are captured.
/// </summary>
internal sealed class GmcpStats
{
    private static readonly string[] PreferredOrder =
        ["hp", "maxhp", "mp", "maxmp", "sp", "maxsp", "level", "xp", "gold"];

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Merges a GMCP message. Returns true if any stat changed.</summary>
    public bool Update(string package, string json)
    {
        if (!package.StartsWith("Char", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var changed = false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                continue;
            }

            // JsonElement.ToString() yields "True"/"False" for booleans; normalise to JSON casing
            // so booleans read consistently with numeric/string fields.
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.Value.ToString(),
            };
            if (!_values.TryGetValue(property.Name, out var existing) || existing != value)
            {
                _values[property.Name] = value;
                changed = true;
            }
        }

        return changed;
    }

    public bool HasData => _values.Count > 0;

    /// <summary>Reads an integer stat (e.g. <c>hp</c>, <c>maxhp</c>), or null if absent/non-numeric.</summary>
    public int? GetInt(string key) =>
        _values.TryGetValue(key, out var value) && int.TryParse(value, out var n) ? n : null;

    /// <summary>A compact one-line summary, preferred vitals first.</summary>
    public string Summarize()
    {
        if (_values.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var key in PreferredOrder)
        {
            if (_values.TryGetValue(key, out var value))
            {
                Append(sb, key, value);
            }
        }

        foreach (var (key, value) in _values)
        {
            if (Array.IndexOf(PreferredOrder, key.ToLowerInvariant()) < 0)
            {
                Append(sb, key, value);
            }
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0)
        {
            sb.Append("  ");
        }

        sb.Append(key).Append(':').Append(value);
    }
}
