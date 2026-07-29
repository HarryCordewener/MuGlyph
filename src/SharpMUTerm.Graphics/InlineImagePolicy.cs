namespace SharpMUTerm.Graphics;

/// <summary>
/// How an inline image is actually put on screen, once both the terminal's capabilities and the
/// limits of the code drawing into it have been taken into account. Ordered by ascending fidelity
/// so a caller can compare presentations the way it compares <see cref="GraphicsProtocol"/>.
/// </summary>
public enum InlineImagePresentation
{
    /// <summary>Nothing can be drawn; the caller's text placeholder stands in for the image.</summary>
    TextPlaceholder = 0,

    /// <summary>Styled ▀ half-block cells — two stacked pixels per cell, works in any colour terminal.</summary>
    HalfBlock = 1,

    /// <summary>A DEC Sixel escape sequence written straight at the cursor.</summary>
    Sixel = 2,

    /// <summary>Kitty graphics: the image is transmitted once and shown through placeholder cells.</summary>
    Kitty = 3,
}

/// <summary>
/// What the code doing the drawing can physically emit. <see cref="TerminalCapabilities"/> says what
/// the <em>terminal</em> can display; this says what the <em>render host</em> can hand it, and the two
/// are not the same thing.
///
/// <para>The distinction is load-bearing for a compositor-based TUI. A compositor owns every cell and
/// re-diffs the screen each frame, so it can carry Kitty images (they live in real character cells as
/// U+10EEEE placeholders, which scroll and clip like text) and half-blocks (ordinary styled glyphs),
/// but it has nowhere to put a raw Sixel blob: Sixel paints at the cursor outside the cell model, and
/// the next repaint would scribble over it. A surface that writes to the terminal directly, with no
/// compositor in between, has the opposite trade-off.</para>
/// </summary>
public sealed class GraphicsSurface
{
    private GraphicsSurface(bool canPlaceKitty, bool canWriteRawEscapes, bool canStyleCells)
    {
        CanPlaceKitty = canPlaceKitty;
        CanWriteRawEscapes = canWriteRawEscapes;
        CanStyleCells = canStyleCells;
    }

    /// <summary>True when the host can anchor a Kitty image into character cells.</summary>
    public bool CanPlaceKitty { get; }

    /// <summary>True when the host can emit a raw escape sequence (Sixel) that survives to the next frame.</summary>
    public bool CanWriteRawEscapes { get; }

    /// <summary>True when the host can emit foreground/background-coloured cells (needed for half-blocks).</summary>
    public bool CanStyleCells { get; }

    /// <summary>
    /// A compositor-based TUI such as SharpConsoleUI: coloured cells always, Kitty placements when
    /// the driver reports the protocol, and never raw escapes.
    /// </summary>
    /// <param name="canPlaceKitty">Whether the console driver reports Kitty graphics support.</param>
    public static GraphicsSurface Compositor(bool canPlaceKitty) => new(canPlaceKitty, false, true);

    /// <summary>Direct terminal output with no compositor in the way: anything the terminal accepts.</summary>
    public static GraphicsSurface RawTerminal { get; } = new(true, true, true);

    /// <summary>A plain-text sink (a log file, an ANSI snapshot, a pipe): only the placeholder survives.</summary>
    public static GraphicsSurface PlainText { get; } = new(false, false, false);
}

/// <summary>
/// The degradation chain: Kitty → Sixel → half-block → text placeholder, with every rung the host
/// cannot deliver skipped rather than attempted. Pure, so the whole matrix is unit-testable without a
/// terminal — which matters, because the fallbacks are precisely the part no headless run can see.
/// </summary>
public static class InlineImagePolicy
{
    /// <summary>
    /// Picks the best presentation the terminal <em>and</em> the host can both manage.
    /// </summary>
    /// <remarks>
    /// Selection keys off <see cref="TerminalCapabilities.Protocol"/> rather than the individual
    /// capability flags, so an explicit <c>SHARPMUTERM_GRAPHICS</c> override still wins — the user
    /// asked for it, and forcing a protocol on a terminal we failed to sniff is a legitimate thing
    /// to want. Stepping <em>down</em> the chain is always safe: a terminal that speaks Kitty can
    /// also draw half-blocks.
    /// </remarks>
    public static InlineImagePresentation Select(TerminalCapabilities capabilities, GraphicsSurface surface)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(surface);

        if (capabilities.Protocol >= GraphicsProtocol.Kitty && surface.CanPlaceKitty)
        {
            return InlineImagePresentation.Kitty;
        }

        if (capabilities.Protocol >= GraphicsProtocol.Sixel && surface.CanWriteRawEscapes)
        {
            return InlineImagePresentation.Sixel;
        }

        if (capabilities.Protocol >= GraphicsProtocol.HalfBlock && surface.CanStyleCells)
        {
            return InlineImagePresentation.HalfBlock;
        }

        return InlineImagePresentation.TextPlaceholder;
    }

    /// <summary>
    /// A one-line, user-facing account of the choice, naming the reason whenever the host had to
    /// degrade below what the terminal itself offers. Shown by <c>/graphics</c> so a puzzled user
    /// gets "Sixel is not available inside the TUI" instead of a silently worse picture.
    /// </summary>
    public static string Describe(TerminalCapabilities capabilities, GraphicsSurface surface)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(surface);

        var chosen = Select(capabilities, surface);
        var detected = capabilities.Protocol;

        if (chosen == InlineImagePresentation.TextPlaceholder)
        {
            return detected == GraphicsProtocol.None
                ? "no inline graphics detected — images show as text placeholders"
                : $"{detected} detected, but this view cannot draw images — text placeholders only";
        }

        if ((int)chosen == (int)detected)
        {
            return $"inline images render via {chosen}";
        }

        // The terminal offers more than the host can carry. Name the rung that was skipped.
        var reason = detected == GraphicsProtocol.Sixel && !surface.CanWriteRawEscapes
            ? "Sixel cannot be drawn inside the compositor"
            : $"{detected} is unavailable here";

        return $"inline images render via {chosen} ({reason})";
    }
}
