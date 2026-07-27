namespace MuClient.Core.Text;

/// <summary>How a <see cref="TerminalColor"/> value should be interpreted.</summary>
public enum TerminalColorKind : byte
{
    /// <summary>The terminal's default foreground/background colour.</summary>
    Default,

    /// <summary>A palette index in the range 0-255 (16 ANSI + 216 cube + 24 greys).</summary>
    Indexed,

    /// <summary>A 24-bit truecolour value.</summary>
    Rgb,
}

/// <summary>
/// A colour as expressed by an ANSI SGR sequence: the terminal default, a palette
/// index (0-255), or a 24-bit RGB triple. UI-agnostic — the renderer decides how to
/// realise an index against a concrete palette.
/// </summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    private TerminalColor(TerminalColorKind kind, byte index, byte r, byte g, byte b)
    {
        Kind = kind;
        Index = index;
        R = r;
        G = g;
        B = b;
    }

    /// <summary>How to interpret this colour.</summary>
    public TerminalColorKind Kind { get; }

    /// <summary>Palette index (0-255), valid when <see cref="Kind"/> is <see cref="TerminalColorKind.Indexed"/>.</summary>
    public byte Index { get; }

    /// <summary>Red channel, valid when <see cref="Kind"/> is <see cref="TerminalColorKind.Rgb"/>.</summary>
    public byte R { get; }

    /// <summary>Green channel, valid when <see cref="Kind"/> is <see cref="TerminalColorKind.Rgb"/>.</summary>
    public byte G { get; }

    /// <summary>Blue channel, valid when <see cref="Kind"/> is <see cref="TerminalColorKind.Rgb"/>.</summary>
    public byte B { get; }

    /// <summary>The terminal default colour.</summary>
    public static TerminalColor Default => new(TerminalColorKind.Default, 0, 0, 0, 0);

    /// <summary>A palette colour by index (0-255).</summary>
    public static TerminalColor FromIndex(int index)
    {
        if (index is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Palette index must be 0-255.");
        }

        return new TerminalColor(TerminalColorKind.Indexed, (byte)index, 0, 0, 0);
    }

    /// <summary>A 24-bit truecolour value.</summary>
    public static TerminalColor FromRgb(byte r, byte g, byte b) => new(TerminalColorKind.Rgb, 0, r, g, b);

    public bool Equals(TerminalColor other) => Kind switch
    {
        TerminalColorKind.Default => other.Kind == TerminalColorKind.Default,
        TerminalColorKind.Indexed => other.Kind == TerminalColorKind.Indexed && Index == other.Index,
        TerminalColorKind.Rgb => other.Kind == TerminalColorKind.Rgb && R == other.R && G == other.G && B == other.B,
        _ => false,
    };

    public override bool Equals(object? obj) => obj is TerminalColor other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        TerminalColorKind.Indexed => HashCode.Combine(Kind, Index),
        TerminalColorKind.Rgb => HashCode.Combine(Kind, R, G, B),
        _ => HashCode.Combine(Kind),
    };

    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);

    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        TerminalColorKind.Default => "default",
        TerminalColorKind.Indexed => $"idx({Index})",
        TerminalColorKind.Rgb => $"rgb({R},{G},{B})",
        _ => "?",
    };
}
