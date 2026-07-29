using SharpMUTerm.Web;

namespace SharpMUTerm.Tui;

/// <summary>One run of a rendered web page: either a stretch of markup lines or an inline image.</summary>
internal abstract record WebBlock;

/// <summary>A run of consecutive markup lines, rendered by a single markup control.</summary>
/// <param name="Lines">The markup lines, in order.</param>
internal sealed record WebTextBlock(IReadOnlyList<string> Lines) : WebBlock;

/// <summary>
/// An image that replaces its placeholder line. Carries the cell box the picture will occupy so the
/// host can reserve exactly that much room.
/// </summary>
/// <param name="Index">Index into the page's image list, so the host can find the decoded pixels.</param>
/// <param name="Image">The source image, as found by the HTML renderer.</param>
/// <param name="Box">The cell box the image claims.</param>
internal sealed record WebImageBlock(int Index, WebImage Image, WebImageLayout.CellBox Box) : WebBlock;

/// <summary>
/// Splits a rendered web page into the alternating text/image runs the web view is built from.
/// Pure: it is handed the lines, the image index, and the boxes of the images that actually decoded,
/// and it decides nothing about protocols or terminals.
///
/// <para>The degraded case is the important one and it is deliberately the cheapest: with no
/// renderable images the result is a single text block holding every line, byte-identical to the
/// pre-existing single-control web view. Placeholders stay exactly where the HTML renderer put
/// them.</para>
/// </summary>
internal static class WebViewComposer
{
    /// <summary>
    /// Composes the page into blocks.
    /// </summary>
    /// <param name="markupLines">The page's lines, already converted to markup.</param>
    /// <param name="images">Every indexed image on the page, in document order.</param>
    /// <param name="boxes">
    /// Cell boxes keyed by index into <paramref name="images"/>, holding only the images that
    /// decoded and are worth drawing. Images missing from this map keep their placeholder line.
    /// </param>
    public static IReadOnlyList<WebBlock> Compose(
        IReadOnlyList<string> markupLines,
        IReadOnlyList<WebImage> images,
        IReadOnlyDictionary<int, WebImageLayout.CellBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(markupLines);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(boxes);

        // Only images that decoded, point at a real line, and haven't already claimed those lines.
        // The claim covers the whole placeholder, not just its first line: a long alt text wraps, and
        // leaving the tail behind would strand it under the picture.
        var claimed = new HashSet<int>();
        var placements = new List<(int Line, int Count, WebImageBlock Block)>();
        for (var i = 0; i < images.Count; i++)
        {
            if (!boxes.TryGetValue(i, out var box))
            {
                continue;
            }

            var line = images[i].LineIndex;
            if (line < 0 || line >= markupLines.Count)
            {
                continue;
            }

            // A trailing-blank trim can leave the recorded span longer than the page it points into.
            var count = Math.Clamp(images[i].LineCount, 1, markupLines.Count - line);
            if (Enumerable.Range(line, count).Any(claimed.Contains))
            {
                continue;
            }

            for (var l = line; l < line + count; l++)
            {
                claimed.Add(l);
            }

            placements.Add((line, count, new WebImageBlock(i, images[i], box)));
        }

        if (placements.Count == 0)
        {
            // Nothing to draw: one control, one list of lines — the plain text-mode page.
            return new WebBlock[] { new WebTextBlock(markupLines) };
        }

        placements.Sort(static (a, b) => a.Line.CompareTo(b.Line));

        var blocks = new List<WebBlock>();
        var cursor = 0;
        foreach (var (line, count, block) in placements)
        {
            if (line > cursor)
            {
                blocks.Add(new WebTextBlock(Slice(markupLines, cursor, line)));
            }

            blocks.Add(block);
            cursor = line + count; // every placeholder line is consumed by the picture
        }

        if (cursor < markupLines.Count)
        {
            blocks.Add(new WebTextBlock(Slice(markupLines, cursor, markupLines.Count)));
        }

        return blocks;
    }

    private static IReadOnlyList<string> Slice(IReadOnlyList<string> lines, int start, int end)
    {
        var slice = new string[end - start];
        for (var i = 0; i < slice.Length; i++)
        {
            slice[i] = lines[start + i];
        }

        return slice;
    }
}
