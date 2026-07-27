using System.Text.Json;
using System.Text.Json.Serialization;
using MuClient.Core.Text;

namespace MuClient.Core.Configuration;

/// <summary>
/// Serialises <see cref="TerminalColor"/> as a compact string: <c>default</c>, <c>idx:N</c>,
/// or <c>rgb:R,G,B</c>.
/// </summary>
public sealed class TerminalColorJsonConverter : JsonConverter<TerminalColor>
{
    public override TerminalColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return Parse(text);
    }

    public static TerminalColor Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return TerminalColor.Default;
        }

        if (text.StartsWith("idx:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text.AsSpan(4), out var index))
        {
            return TerminalColor.FromIndex(Math.Clamp(index, 0, 255));
        }

        if (text.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text[4..].Split(',');
            if (parts.Length == 3 &&
                byte.TryParse(parts[0], out var r) &&
                byte.TryParse(parts[1], out var g) &&
                byte.TryParse(parts[2], out var b))
            {
                return TerminalColor.FromRgb(r, g, b);
            }
        }

        return TerminalColor.Default;
    }

    public override void Write(Utf8JsonWriter writer, TerminalColor value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(ToString(value));
    }

    public static string ToString(TerminalColor value) => value.Kind switch
    {
        TerminalColorKind.Indexed => $"idx:{value.Index}",
        TerminalColorKind.Rgb => $"rgb:{value.R},{value.G},{value.B}",
        _ => "default",
    };
}
