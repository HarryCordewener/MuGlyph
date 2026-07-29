using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Sizing rules for inline web images: what is worth drawing, and how big it may get. Pure
/// arithmetic, so it is verifiable without a terminal — unlike the picture it eventually produces.
/// </summary>
public class WebImageLayoutTests
{
    // ---- Worth drawing at all? ---------------------------------------------------------------

    [Test]
    [Arguments(1, 1)]
    [Arguments(1, 200)]
    [Arguments(200, 1)]
    [Arguments(15, 15)]
    [Arguments(15, 400)]
    [Arguments(0, 0)]
    public async Task TinyOrDegenerateImages_AreNotWorthDrawing(int width, int height)
    {
        // Spacer gifs, tracking pixels, and rules read better as their text placeholder.
        await Assert.That(WebImageLayout.IsWorthRendering(width, height)).IsFalse();
    }

    [Test]
    [Arguments(16, 16)]
    [Arguments(64, 64)]
    [Arguments(1920, 1080)]
    public async Task RealImages_AreWorthDrawing(int width, int height)
    {
        await Assert.That(WebImageLayout.IsWorthRendering(width, height)).IsTrue();
    }

    [Test]
    public async Task NegativeDimensions_AreNotWorthDrawing()
    {
        await Assert.That(WebImageLayout.IsWorthRendering(-10, -10)).IsFalse();
    }

    // ---- Fitting ------------------------------------------------------------------------------

    [Test]
    public async Task SmallImage_KeepsItsNaturalCellSizeRatherThanBeingBlownUp()
    {
        // Fit never upscales, matching the framework's ImageScaleMode.Fit.
        var box = WebImageLayout.Fit(40, 40, availableColumns: 200);
        await Assert.That(box.Columns).IsEqualTo(40);
        await Assert.That(box.Rows).IsEqualTo(20);
    }

    [Test]
    public async Task WideImage_IsClampedToTheAvailableColumns()
    {
        // 400x200 → natural 400 cols x 100 rows. Raise the row ceiling so the column budget is what
        // actually binds: scale 100/400 → 100 cols x 25 rows.
        var box = WebImageLayout.Fit(400, 200, availableColumns: 100, maxRows: 100);
        await Assert.That(box.Columns).IsEqualTo(100);
        await Assert.That(box.Rows).IsEqualTo(25);
    }

    [Test]
    public async Task RowCeilingBindsBeforeTheColumnBudgetOnATallImage()
    {
        // Same image at the default 20-row ceiling: 20/100 is a tighter scale than 100/400, so the
        // ceiling wins and the image ends up narrower than the columns on offer.
        var box = WebImageLayout.Fit(400, 200, availableColumns: 100);
        await Assert.That(box.Rows).IsEqualTo(WebImageLayout.MaxRows);
        await Assert.That(box.Columns).IsEqualTo(80);
    }

    [Test]
    public async Task TallImage_IsClampedToTheRowCeiling()
    {
        var box = WebImageLayout.Fit(100, 4000, availableColumns: 200);
        await Assert.That(box.Rows).IsLessThanOrEqualTo(WebImageLayout.MaxRows);
    }

    [Test]
    public async Task NoSingleImageMayExceedTheRowCeiling()
    {
        foreach (var (w, h) in new[] { (100, 100), (2000, 2000), (32, 4000), (4000, 32), (16, 16) })
        {
            var box = WebImageLayout.Fit(w, h, availableColumns: 120);
            await Assert.That(box.Rows)
                .IsLessThanOrEqualTo(WebImageLayout.MaxRows)
                .Because($"{w}x{h} must not claim more than {WebImageLayout.MaxRows} rows");
        }
    }

    [Test]
    public async Task FitNeverExceedsTheColumnBudget()
    {
        foreach (var columns in new[] { 1, 10, 40, 120, 200 })
        {
            var box = WebImageLayout.Fit(800, 600, columns);
            await Assert.That(box.Columns).IsLessThanOrEqualTo(columns);
        }
    }

    [Test]
    public async Task AspectRatioIsHeldWithinARoundingCell()
    {
        // 800x600 → natural 800 cols x 300 rows, an 8:3 cell ratio; keep it across the downscale.
        var box = WebImageLayout.Fit(800, 600, availableColumns: 80, maxRows: 100);
        await Assert.That(box.Columns).IsEqualTo(80);
        await Assert.That(box.Rows).IsEqualTo(30);
    }

    [Test]
    public async Task OddPixelHeight_RoundsUpToACoveringRow()
    {
        // 20x21 pixels needs 11 half-block rows, not 10 — the last row is half used.
        var box = WebImageLayout.Fit(20, 21, availableColumns: 200, maxRows: 100);
        await Assert.That(box.Rows).IsEqualTo(11);
    }

    [Test]
    public async Task FitAlwaysClaimsAtLeastOneCell()
    {
        var box = WebImageLayout.Fit(4000, 16, availableColumns: 1, maxRows: 1);
        await Assert.That(box.Columns).IsGreaterThanOrEqualTo(1);
        await Assert.That(box.Rows).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    [Arguments(0, 100, 80, 20)]
    [Arguments(100, 0, 80, 20)]
    [Arguments(100, 100, 0, 20)]
    [Arguments(100, 100, 80, 0)]
    [Arguments(-1, -1, 80, 20)]
    public async Task DegenerateInputs_YieldAnEmptyBox(int w, int h, int columns, int maxRows)
    {
        var box = WebImageLayout.Fit(w, h, columns, maxRows);
        await Assert.That(box.Columns).IsEqualTo(0);
        await Assert.That(box.Rows).IsEqualTo(0);
    }
}
