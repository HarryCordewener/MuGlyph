using System.Text.Json;
using System.Text.Json.Serialization;
using MuClient.Core.Text;

namespace MuClient.Core.Configuration;

/// <summary>Serialises <see cref="Rgb"/> as a CSS-style hex string (<c>#rrggbb</c>).</summary>
public sealed class RgbJsonConverter : JsonConverter<Rgb>
{
    public override Rgb Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Parse(reader.GetString());

    public static Rgb Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Rgb(0, 0, 0);
        }

        var hex = text.TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return new Rgb(r, g, b);
        }

        return new Rgb(0, 0, 0);
    }

    public override void Write(Utf8JsonWriter writer, Rgb value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToHex());
}
