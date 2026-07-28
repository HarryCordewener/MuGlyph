namespace SharpMUTerm.Tui;

/// <summary>
/// Renders a spawn window's capture line — the dim <c>⇱ capture ^\[public\]</c> row shown under the
/// tab strip that tells you which trigger pattern routes lines into the window (design pane area).
/// Pure so the markup is unit-testable without a terminal.
/// </summary>
internal static class CaptureLineRenderer
{
    public static string Line(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return $"[dim]{Glyphs.Capture} capture {Escape(pattern)}[/]";
    }

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
