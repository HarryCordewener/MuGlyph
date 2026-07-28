namespace SharpMUTerm.Core.Text;

/// <summary>An 8-bit-per-channel RGB triple.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Returns the colour as a CSS/HTML hex string, e.g. <c>#1a2b3c</c>.</summary>
    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";
}

/// <summary>
/// Resolves ANSI palette indices (0-255) to concrete RGB values using the standard xterm
/// palette: 16 system colours, a 6×6×6 colour cube (16-231), and a 24-step greyscale ramp
/// (232-255). Used for HTML logging and any renderer that needs true RGB from an index.
/// </summary>
public static class AnsiPalette
{
    private static readonly Rgb[] System16 =
    [
        new(0x00, 0x00, 0x00), new(0x80, 0x00, 0x00), new(0x00, 0x80, 0x00), new(0x80, 0x80, 0x00),
        new(0x00, 0x00, 0x80), new(0x80, 0x00, 0x80), new(0x00, 0x80, 0x80), new(0xc0, 0xc0, 0xc0),
        new(0x80, 0x80, 0x80), new(0xff, 0x00, 0x00), new(0x00, 0xff, 0x00), new(0xff, 0xff, 0x00),
        new(0x00, 0x00, 0xff), new(0xff, 0x00, 0xff), new(0x00, 0xff, 0xff), new(0xff, 0xff, 0xff),
    ];

    private static readonly byte[] CubeLevels = [0, 95, 135, 175, 215, 255];

    /// <summary>Maps a palette index (0-255) to its xterm RGB value.</summary>
    public static Rgb ToRgb(int index)
    {
        if (index is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Palette index must be 0-255.");
        }

        if (index < 16)
        {
            return System16[index];
        }

        if (index < 232)
        {
            var n = index - 16;
            var r = CubeLevels[n / 36 % 6];
            var g = CubeLevels[n / 6 % 6];
            var b = CubeLevels[n % 6];
            return new Rgb(r, g, b);
        }

        var grey = (byte)(8 + (index - 232) * 10);
        return new Rgb(grey, grey, grey);
    }

    /// <summary>
    /// Resolves a <see cref="TerminalColor"/> to RGB. Default colours resolve to the supplied
    /// fallback (typically the theme's foreground/background).
    /// </summary>
    public static Rgb Resolve(TerminalColor colour, Rgb fallback) => colour.Kind switch
    {
        TerminalColorKind.Rgb => new Rgb(colour.R, colour.G, colour.B),
        TerminalColorKind.Indexed => ToRgb(colour.Index),
        _ => fallback,
    };
}
