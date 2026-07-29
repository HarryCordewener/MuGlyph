namespace SharpMUTerm.Tui;

/// <summary>
/// Cell arithmetic for an inline web image: whether a decoded image is worth drawing at all, and the
/// cell box it should occupy. Pure, so the sizing rules are unit-testable without a terminal — and
/// they need to be, because getting them wrong is how one photo eats a whole page.
///
/// <para>The unit conversion throughout is the half-block one the framework also uses: a character
/// cell carries one pixel horizontally and two vertically, which lands close enough to square given
/// how much taller than wide a terminal cell is.</para>
/// </summary>
internal static class WebImageLayout
{
    /// <summary>Vertical pixels carried by one character cell (the ▀ half-block split).</summary>
    public const int PixelsPerCell = 2;

    /// <summary>Tallest an inline image may be, so one picture cannot push a whole article offscreen.</summary>
    public const int MaxRows = 20;

    /// <summary>
    /// Smallest edge worth drawing. Below this an image is a spacer, a tracking pixel, or a bullet
    /// glyph — all of which read better as their text placeholder than as a smear of colour.
    /// </summary>
    public const int MinimumPixelEdge = 16;

    /// <summary>A cell box for an inline image.</summary>
    /// <param name="Columns">Width in character cells.</param>
    /// <param name="Rows">Height in character cells.</param>
    internal readonly record struct CellBox(int Columns, int Rows);

    /// <summary>
    /// True when a decoded image is large enough to be worth showing. Rejects anything with a
    /// degenerate or tiny edge.
    /// </summary>
    public static bool IsWorthRendering(int pixelWidth, int pixelHeight) =>
        pixelWidth >= MinimumPixelEdge && pixelHeight >= MinimumPixelEdge;

    /// <summary>
    /// Fits an image into the available cell box, preserving aspect ratio and never upscaling —
    /// matching the framework's <c>ImageScaleMode.Fit</c>, so the box we reserve is the box the
    /// control actually claims.
    /// </summary>
    /// <param name="pixelWidth">Decoded image width in pixels.</param>
    /// <param name="pixelHeight">Decoded image height in pixels.</param>
    /// <param name="availableColumns">Columns the pane can spare.</param>
    /// <param name="maxRows">Row ceiling; defaults to <see cref="MaxRows"/>.</param>
    public static CellBox Fit(int pixelWidth, int pixelHeight, int availableColumns, int maxRows = MaxRows)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || availableColumns <= 0 || maxRows <= 0)
        {
            return new CellBox(0, 0);
        }

        var naturalColumns = pixelWidth;
        var naturalRows = (pixelHeight + PixelsPerCell - 1) / PixelsPerCell;

        // Fit never enlarges, so the usable box is clamped to the image's own size first.
        var boundedColumns = Math.Min(naturalColumns, availableColumns);
        var boundedRows = Math.Min(naturalRows, maxRows);

        var scale = Math.Min(
            (double)boundedColumns / naturalColumns,
            (double)boundedRows / naturalRows);

        return new CellBox(
            Math.Max(1, (int)(naturalColumns * scale)),
            Math.Max(1, (int)(naturalRows * scale)));
    }
}
