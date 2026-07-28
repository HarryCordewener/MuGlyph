using System.Text.RegularExpressions;

namespace SharpMUTerm.Tui;

/// <summary>
/// Helpers for the Spectre-style markup the renderers emit: escaping literal brackets, measuring a
/// string's printable width past its <c>[tag]</c> wrappers, and laying text out to that width. Every
/// renderer that pads or right-aligns markup needs the same measurement, so it lives here rather than
/// being copied per screen — a divergent copy silently mis-pads a column.
/// </summary>
internal static class MarkupText
{
    private static readonly Regex TagPattern = new(@"\[[^\[\]]*\]", RegexOptions.Compiled);

    /// <summary>Escapes literal brackets so markup can't be injected by configured text.</summary>
    internal static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");

    /// <summary>
    /// Counts the printable length of a markup string: escaped brackets (<c>[[</c>/<c>]]</c>) count
    /// as one literal character each, and <c>[tag]</c> wrappers are stripped entirely.
    /// </summary>
    internal static int VisibleLength(string markup)
    {
        var protectedText = markup.Replace("[[", "\u0001").Replace("]]", "\u0002");
        return TagPattern.Replace(protectedText, string.Empty).Length;
    }

    /// <summary>Pads a markup string to a target *visible* column width, ignoring markup tags.</summary>
    internal static string PadVisible(string markup, int width)
    {
        var visible = VisibleLength(markup);
        return visible >= width ? markup : markup + new string(' ', width - visible);
    }

    /// <summary>
    /// Lays a left- and right-hand fragment on one line, right-aligning the right to
    /// <paramref name="width"/>. A non-positive width means "width unknown" (the unit-test path), which
    /// falls back to a fixed gap.
    /// </summary>
    internal static string SpreadLR(string left, string right, int width)
    {
        if (width <= 0)
        {
            return $"{left}   {right}";
        }

        var gap = Math.Max(1, width - VisibleLength(left) - VisibleLength(right));
        return left + new string(' ', gap) + right;
    }
}
