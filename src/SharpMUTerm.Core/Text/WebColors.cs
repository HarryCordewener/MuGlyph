namespace SharpMUTerm.Core.Text;

/// <summary>
/// Resolves the named colours used by MXP and Pueblo markup (a subset of CSS/HTML colour names,
/// plus <c>#rrggbb</c>) to a <see cref="TerminalColor"/>.
/// </summary>
public static class WebColors
{
    private static readonly IReadOnlyDictionary<string, Rgb> Named = new Dictionary<string, Rgb>(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = new(0x00, 0x00, 0x00),
        ["silver"] = new(0xc0, 0xc0, 0xc0),
        ["gray"] = new(0x80, 0x80, 0x80),
        ["grey"] = new(0x80, 0x80, 0x80),
        ["white"] = new(0xff, 0xff, 0xff),
        ["maroon"] = new(0x80, 0x00, 0x00),
        ["red"] = new(0xff, 0x00, 0x00),
        ["purple"] = new(0x80, 0x00, 0x80),
        ["fuchsia"] = new(0xff, 0x00, 0xff),
        ["magenta"] = new(0xff, 0x00, 0xff),
        ["green"] = new(0x00, 0x80, 0x00),
        ["lime"] = new(0x00, 0xff, 0x00),
        ["olive"] = new(0x80, 0x80, 0x00),
        ["yellow"] = new(0xff, 0xff, 0x00),
        ["navy"] = new(0x00, 0x00, 0x80),
        ["blue"] = new(0x00, 0x00, 0xff),
        ["teal"] = new(0x00, 0x80, 0x80),
        ["aqua"] = new(0x00, 0xff, 0xff),
        ["cyan"] = new(0x00, 0xff, 0xff),
        ["orange"] = new(0xff, 0xa5, 0x00),
        ["brown"] = new(0xa5, 0x2a, 0x2a),
        ["pink"] = new(0xff, 0xc0, 0xcb),
        ["gold"] = new(0xff, 0xd7, 0x00),
    };

    /// <summary>Tries to resolve a colour name or <c>#rrggbb</c>/<c>#rgb</c> literal.</summary>
    public static bool TryParse(string? value, out TerminalColor color)
    {
        color = TerminalColor.Default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();

        if (value[0] == '#')
        {
            return TryParseHex(value.AsSpan(1), out color);
        }

        if (Named.TryGetValue(value, out var rgb))
        {
            color = TerminalColor.FromRgb(rgb.R, rgb.G, rgb.B);
            return true;
        }

        return false;
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, out TerminalColor color)
    {
        color = TerminalColor.Default;
        const System.Globalization.NumberStyles h = System.Globalization.NumberStyles.HexNumber;

        if (hex.Length == 6 &&
            byte.TryParse(hex[..2], h, null, out var r) &&
            byte.TryParse(hex[2..4], h, null, out var g) &&
            byte.TryParse(hex[4..6], h, null, out var b))
        {
            color = TerminalColor.FromRgb(r, g, b);
            return true;
        }

        // Short form #rgb → #rrggbb.
        if (hex.Length == 3 &&
            byte.TryParse(hex[..1], h, null, out var sr) &&
            byte.TryParse(hex[1..2], h, null, out var sg) &&
            byte.TryParse(hex[2..3], h, null, out var sb))
        {
            color = TerminalColor.FromRgb((byte)(sr * 17), (byte)(sg * 17), (byte)(sb * 17));
            return true;
        }

        return false;
    }
}
