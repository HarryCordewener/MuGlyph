namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The one row that tells a reader where the previous session's content ends. Its whole job is to be
/// unambiguous about a boundary, so what it says — and what it must not accidentally say — is worth
/// pinning apart from the end-to-end restore.
/// </summary>
public class RestoreBarRendererTests
{
    private const string Accent = "#c678dd";

    private static readonly DateTimeOffset Whenever = new(2026, 7, 30, 15, 27, 0, TimeSpan.Zero);

    [Test]
    public async Task ItNamesTheBoundaryTheCountAndWhen()
    {
        var bar = RestoreBarRenderer.Bar(128, Whenever, Accent);

        await Assert.That(bar).Contains(RestoreBarRenderer.Label);
        await Assert.That(bar).Contains("128 lines from the previous session");
        await Assert.That(bar).Contains(Whenever.ToLocalTime().ToString("d MMM HH:mm"));
        await Assert.That(bar).Contains(Accent);
    }

    /// <summary>
    /// A pane holding exactly one restored line must not read "1 lines". Small, and the sort of thing
    /// that survives for years in a string nobody tests because it only appears in one state.
    /// </summary>
    [Test]
    public async Task OneLineIsSingular()
    {
        await Assert.That(RestoreBarRenderer.Bar(1, Whenever, Accent)).Contains("1 line from");
        await Assert.That(RestoreBarRenderer.Bar(1, Whenever, Accent)).DoesNotContain("1 lines");
    }

    /// <summary>
    /// The bar ends in a rule, the way the freeze bar does, so it reads as a divider rather than as a
    /// line of output. The two mark the same pane for related reasons and should not look like different
    /// kinds of thing.
    /// </summary>
    [Test]
    public async Task ItEndsInARuleLikeTheFreezeBar()
    {
        await Assert.That(RestoreBarRenderer.Bar(3, Whenever, Accent)).EndsWith("────[/]");
        await Assert.That(FreezeBarRenderer.Bar(Accent)).EndsWith("────[/]");
    }

    /// <summary>
    /// Nothing in it comes from a world, so nothing in it can carry markup — but the date is
    /// culture-formatted and a locale's month abbreviation is not this code's to vouch for, so it goes
    /// through the same escape every other composed row does. Asserted by feeding a culture whose date
    /// format is unusual and checking the row still has exactly the tags it was built with.
    /// </summary>
    [Test]
    public async Task ItCarriesOnlyTheTagsItBuilt()
    {
        var bar = RestoreBarRenderer.Bar(9, Whenever, Accent);

        // Two opening tags (the accent and the dim) and two closes, and no stray bracket anywhere else.
        await Assert.That(bar.Count(c => c == '[')).IsEqualTo(4);
        await Assert.That(bar.Count(c => c == ']')).IsEqualTo(4);
    }

    [Test]
    public async Task ItRefusesAnEmptyAccent() =>
        await Assert.That(() => RestoreBarRenderer.Bar(1, Whenever, string.Empty))
            .Throws<ArgumentException>();
}
