namespace SharpMUTerm.Graphics;

/// <summary>
/// An inline-graphics transport, ordered by ascending capability. A renderer should
/// pick the highest protocol the terminal actually supports and fall back downward.
/// </summary>
public enum GraphicsProtocol
{
    /// <summary>No inline graphics; only a textual placeholder can be shown.</summary>
    None = 0,

    /// <summary>Universal fallback: two stacked pixels per cell via the ▀ half-block glyph.</summary>
    HalfBlock = 1,

    /// <summary>DEC Sixel raster graphics.</summary>
    Sixel = 2,

    /// <summary>The Kitty graphics protocol (best fidelity, cell-anchored placeholders).</summary>
    Kitty = 3,
}
