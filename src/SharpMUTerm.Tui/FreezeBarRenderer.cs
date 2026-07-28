namespace SharpMUTerm.Tui;

/// <summary>
/// Renders the freeze split bar (<c>▲ FROZEN ⌃F</c>) that divides a frozen pane's pinned scrollback
/// (above) from its live tail (below), per the design's freeze interaction. The label takes the
/// frozen-chrome accent (design token <c>#c678dd</c> / ANSI 5, resolved through the theme); the hint
/// is dim. Pure so the markup is unit-testable without a terminal.
/// </summary>
internal static class FreezeBarRenderer
{
    /// <summary>Builds the frozen-split bar markup from an already-resolved <c>#rrggbb</c> accent.</summary>
    public static string Bar(string accentHex)
    {
        ArgumentException.ThrowIfNullOrEmpty(accentHex);

        // A single line that is both the label and the divider border between pinned scrollback and the
        // live tail: the accented "❄ FROZEN ⌃F" label followed by a dim rule.
        return $"[{accentHex}]{Glyphs.Freeze} FROZEN ⌃F[/] [dim]{new string('─', 48)}[/]";
    }
}
