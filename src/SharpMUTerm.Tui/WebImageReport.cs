using SharpMUTerm.Graphics;
using SharpMUTerm.Web;

namespace SharpMUTerm.Tui;

/// <summary>
/// The per-image account <c>/graphics</c> prints for the page currently in the web view: which
/// <c>&lt;img&gt;</c> elements became pictures, which kept their text placeholder, and — for the ones
/// that drew — the cell box they claim next to the pixel buffer behind it.
///
/// <para>That last pairing is the point of the whole report. "Images don't load correctly" covers
/// several very different failures, and they are indistinguishable by looking at a terminal: nothing
/// fetched at all, fetched but rejected, drawn in the wrong place, or drawn from far fewer pixels
/// than the cells it fills. Printing the buffer size beside the cell box separates the last one from
/// the rest without needing a graphics-capable terminal to reproduce it.</para>
///
/// <para>Pure, over plain numbers rather than the framework's pixel buffers, so the whole report is
/// unit-testable without a terminal.</para>
/// </summary>
internal static class WebImageReport
{
    /// <summary>A decoded image's buffer size, in pixels.</summary>
    /// <param name="Width">Buffer width in pixels — also the cell columns the image claims.</param>
    /// <param name="Height">Buffer height in pixels; two pixel rows make one cell row.</param>
    internal readonly record struct Decoded(int Width, int Height);

    /// <summary>
    /// Describes the web view's current page, one line per element of the result.
    /// </summary>
    /// <param name="page">The page in the web view, or null when nothing has been opened.</param>
    /// <param name="decoded">Buffers that decoded, keyed by index into <see cref="WebPage.Images"/>.</param>
    /// <param name="presentation">What the degradation chain settled on for this view.</param>
    public static IReadOnlyList<string> Describe(
        WebPage? page,
        IReadOnlyDictionary<int, Decoded> decoded,
        InlineImagePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(decoded);

        if (page is null)
        {
            return new[] { "web view: nothing open yet — try /web <url>" };
        }

        var images = page.Images;
        if (images.Count == 0)
        {
            return new[] { $"web view: {page.Url} has no <img> elements" };
        }

        if (presentation == InlineImagePresentation.TextPlaceholder)
        {
            return new[]
            {
                $"web view: {images.Count} image(s), none fetched — this view draws text placeholders",
            };
        }

        var lines = new List<string>
        {
            $"web view: {images.Count} image(s) — {decoded.Count} drawn, " +
            $"{images.Count - decoded.Count} kept a text placeholder",
        };

        for (var i = 0; i < images.Count; i++)
        {
            var source = images[i].Source;
            if (decoded.TryGetValue(i, out var buffer))
            {
                var rows = Math.Max(1, buffer.Height / WebImageLayout.PixelsPerCell);
                lines.Add(
                    $"  #{i + 1} drawn {buffer.Width}x{rows} cells from a " +
                    $"{buffer.Width}x{buffer.Height} px buffer — {source}");
            }
            else
            {
                lines.Add($"  #{i + 1} placeholder (not fetched or not decodable) — {source}");
            }
        }

        return lines;
    }
}
