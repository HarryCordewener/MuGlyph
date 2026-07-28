using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Graphics;

/// <summary>
/// Renders an <see cref="IImageSource"/> to styled text using the upper-half-block glyph
/// <c>▀</c> (<c>U+2580</c>). Each character cell packs two vertically-stacked pixels: the
/// top pixel becomes the glyph's foreground colour, the bottom pixel the background colour.
/// This doubles vertical resolution and works in any truecolour terminal — the universal
/// fallback that also functions in a headless sandbox.
/// </summary>
public sealed class HalfBlockRenderer
{
    /// <summary>The upper-half-block glyph; foreground fills the top, background the bottom.</summary>
    public const string UpperHalfBlock = "▀";

    /// <summary>
    /// Renders <paramref name="image"/> into at most <paramref name="maxCols"/> columns and
    /// <paramref name="maxRows"/> character rows. One character row represents two pixel rows,
    /// so the pixel grid is scaled to <c>maxCols × (maxRows*2)</c> preserving aspect ratio,
    /// then sampled by nearest-neighbour. Returns one <see cref="StyledLine"/> per character row.
    /// </summary>
    public IReadOnlyList<StyledLine> Render(IImageSource image, int maxCols, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (maxCols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCols), maxCols, "Column budget must be positive.");
        }

        if (maxRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRows), maxRows, "Row budget must be positive.");
        }

        // Target pixel grid. Each character row holds two pixel rows, so the vertical pixel
        // budget is maxRows*2. Preserve aspect ratio within the budget.
        var maxPixelRows = maxRows * 2;
        var (targetCols, targetPixelRows) = FitPreservingAspect(
            image.Width, image.Height, maxCols, maxPixelRows);

        // Round up to an even number of pixel rows so every pixel row pairs into a cell.
        var charRows = (targetPixelRows + 1) / 2;

        var lines = new StyledLine[charRows];
        for (var charRow = 0; charRow < charRows; charRow++)
        {
            var topPixelRow = charRow * 2;
            var bottomPixelRow = topPixelRow + 1;

            var spans = new StyledSpan[targetCols];
            for (var col = 0; col < targetCols; col++)
            {
                var top = SampleNearest(image, col, topPixelRow, targetCols, targetPixelRows);

                // Odd total height: the final cell's bottom pixel does not exist. Fall back
                // to the terminal default background so the block collapses to a solid top.
                var bottom = bottomPixelRow < targetPixelRows
                    ? (TerminalColor?)SampleNearest(image, col, bottomPixelRow, targetCols, targetPixelRows)
                    : null;

                var style = new TextStyle(
                    top,
                    bottom ?? TerminalColor.Default,
                    TextAttributes.None);
                spans[col] = new StyledSpan(UpperHalfBlock, style);
            }

            lines[charRow] = new StyledLine(spans);
        }

        return lines;
    }

    private static TerminalColor SampleNearest(
        IImageSource image, int col, int pixelRow, int targetCols, int targetPixelRows)
    {
        // Nearest-neighbour map from the target grid back into source pixels.
        var srcX = targetCols == 1 ? 0 : col * image.Width / targetCols;
        var srcY = targetPixelRows == 1 ? 0 : pixelRow * image.Height / targetPixelRows;

        if (srcX >= image.Width)
        {
            srcX = image.Width - 1;
        }

        if (srcY >= image.Height)
        {
            srcY = image.Height - 1;
        }

        var p = image.GetPixel(srcX, srcY);
        return TerminalColor.FromRgb(p.R, p.G, p.B);
    }

    private static (int Cols, int PixelRows) FitPreservingAspect(
        int srcWidth, int srcHeight, int maxCols, int maxPixelRows)
    {
        // Scale so neither budget is exceeded, keeping aspect ratio; never upscale.
        var scale = Math.Min(
            Math.Min(1.0, (double)maxCols / srcWidth),
            (double)maxPixelRows / srcHeight);

        var cols = Math.Max(1, (int)Math.Round(srcWidth * scale));
        var pixelRows = Math.Max(1, (int)Math.Round(srcHeight * scale));

        return (Math.Min(cols, maxCols), Math.Min(pixelRows, maxPixelRows));
    }
}
