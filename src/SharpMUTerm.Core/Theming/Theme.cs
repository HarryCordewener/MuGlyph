using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Theming;

/// <summary>
/// A colour theme: semantic UI colours plus an optional override of the 16 base ANSI palette
/// entries. Pure data (no UI dependency) so it can be serialised to the config and applied by
/// any renderer — the TUI maps it to Terminal.Gui attributes, logs map it to CSS.
/// </summary>
public sealed class Theme
{
    public string Name { get; set; } = "Dark";

    /// <summary>Default text foreground (for <see cref="TerminalColorKind.Default"/> foregrounds).</summary>
    public Rgb Foreground { get; set; } = new(0xd0, 0xd0, 0xd0);

    /// <summary>Default text background.</summary>
    public Rgb Background { get; set; } = new(0x1e, 0x1e, 0x1e);

    /// <summary>Status bar foreground.</summary>
    public Rgb StatusForeground { get; set; } = new(0xe0, 0xe0, 0xe0);

    /// <summary>Status bar background.</summary>
    public Rgb StatusBackground { get; set; } = new(0x33, 0x33, 0x44);

    /// <summary>Window border / chrome colour.</summary>
    public Rgb Border { get; set; } = new(0x55, 0x55, 0x66);

    /// <summary>Colour of client system messages (e.g. <c>*** Connected</c>).</summary>
    public Rgb SystemMessage { get; set; } = new(0x6a, 0x99, 0x55);

    /// <summary>Colour of locally-echoed user input.</summary>
    public Rgb LocalEcho { get; set; } = new(0xdc, 0xdc, 0xaa);

    /// <summary>Colour of the prompt line.</summary>
    public Rgb Prompt { get; set; } = new(0x56, 0x9c, 0xd6);

    /// <summary>
    /// Optional override of the 16 base ANSI colours (indices 0-15). When null or the wrong
    /// length, the standard xterm values are used.
    /// </summary>
    public List<Rgb>? Palette16 { get; set; }

    /// <summary>Resolves a palette index (0-255) honouring any <see cref="Palette16"/> override.</summary>
    public Rgb ResolveIndex(int index)
    {
        if (index is >= 0 and < 16 && Palette16 is { Count: 16 })
        {
            return Palette16[index];
        }

        return AnsiPalette.ToRgb(index);
    }

    /// <summary>Resolves a <see cref="TerminalColor"/> to RGB, using theme defaults for default colours.</summary>
    public Rgb Resolve(TerminalColor colour, bool isBackground) => colour.Kind switch
    {
        TerminalColorKind.Rgb => new Rgb(colour.R, colour.G, colour.B),
        TerminalColorKind.Indexed => ResolveIndex(colour.Index),
        _ => isBackground ? Background : Foreground,
    };
}
