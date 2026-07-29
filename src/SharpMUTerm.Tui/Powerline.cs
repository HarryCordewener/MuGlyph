using System.Text;
using static SharpMUTerm.Tui.MarkupText;

namespace SharpMUTerm.Tui;

/// <summary>One coloured segment of a powerline bar: its text and resolved <c>#rrggbb</c> colours.</summary>
internal readonly record struct PowerSegment(string Text, string Fg, string Bg);

/// <summary>
/// Renders Oh-My-Posh-style "powerline" bars: coloured segments joined by solid triangle separators
/// () whose colours blend one segment's background into the next, so the bar reads as a flowing
/// ribbon. Uses Nerd Font glyphs (bundled for snapshots). Pure so the markup is unit-testable; the
/// caller supplies already-resolved colours and plain segment text.
/// </summary>
internal static class Powerline
{
    /// <summary>
    /// A left-anchored bar: segments flow left→right with right-pointing separators, and a final
    /// separator blends the last segment into <paramref name="tailBg"/> (usually the bar's background).
    /// </summary>
    public static string LeftBar(IReadOnlyList<PowerSegment> segments, string tailBg)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            sb.Append('[').Append(s.Fg).Append(" on ").Append(s.Bg).Append("] ").Append(Escape(s.Text)).Append(" [/]");
            var next = i + 1 < segments.Count ? segments[i + 1].Bg : tailBg;
            sb.Append('[').Append(s.Bg).Append(" on ").Append(next).Append(']').Append(Glyphs.PowerRight).Append("[/]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// A right-anchored bar: each segment is preceded by a left-pointing separator that blends the
    /// area to its left into the segment. <paramref name="headBg"/> is the background just left of the bar.
    /// </summary>
    public static string RightBar(IReadOnlyList<PowerSegment> segments, string headBg)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var prev = i == 0 ? headBg : segments[i - 1].Bg;
            sb.Append('[').Append(s.Bg).Append(" on ").Append(prev).Append(']').Append(Glyphs.PowerLeft).Append("[/]");
            sb.Append('[').Append(s.Fg).Append(" on ").Append(s.Bg).Append("] ").Append(Escape(s.Text)).Append(" [/]");
        }

        return sb.ToString();
    }
}
