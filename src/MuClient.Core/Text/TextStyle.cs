namespace MuClient.Core.Text;

/// <summary>Non-colour text rendition attributes carried by an SGR sequence.</summary>
[Flags]
public enum TextAttributes
{
    None = 0,
    Bold = 1 << 0,
    Faint = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Reverse = 1 << 5,
    Conceal = 1 << 6,
    Strikethrough = 1 << 7,
}

/// <summary>
/// An immutable snapshot of terminal rendition state: foreground, background, and
/// attribute flags. Produced by <see cref="AnsiParser"/> and consumed by the renderer.
/// </summary>
public readonly struct TextStyle : IEquatable<TextStyle>
{
    public TextStyle(TerminalColor foreground, TerminalColor background, TextAttributes attributes)
    {
        Foreground = foreground;
        Background = background;
        Attributes = attributes;
    }

    public TerminalColor Foreground { get; }

    public TerminalColor Background { get; }

    public TextAttributes Attributes { get; }

    /// <summary>The default rendition: default colours, no attributes.</summary>
    public static TextStyle Default => new(TerminalColor.Default, TerminalColor.Default, TextAttributes.None);

    public bool HasAttribute(TextAttributes attribute) => (Attributes & attribute) == attribute;

    public TextStyle WithForeground(TerminalColor foreground) => new(foreground, Background, Attributes);

    public TextStyle WithBackground(TerminalColor background) => new(Foreground, background, Attributes);

    public TextStyle WithAttributes(TextAttributes attributes) => new(Foreground, Background, attributes);

    public TextStyle AddAttribute(TextAttributes attribute) => new(Foreground, Background, Attributes | attribute);

    public TextStyle RemoveAttribute(TextAttributes attribute) => new(Foreground, Background, Attributes & ~attribute);

    public bool Equals(TextStyle other) =>
        Foreground.Equals(other.Foreground) &&
        Background.Equals(other.Background) &&
        Attributes == other.Attributes;

    public override bool Equals(object? obj) => obj is TextStyle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Foreground, Background, Attributes);

    public static bool operator ==(TextStyle left, TextStyle right) => left.Equals(right);

    public static bool operator !=(TextStyle left, TextStyle right) => !left.Equals(right);
}
