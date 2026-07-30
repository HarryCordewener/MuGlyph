using SharpMUTerm.Tui;
using static SharpMUTerm.Tui.MarkupText;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The ⌃B which-key panel's own rules: which commands it calls live against a given workspace, what it
/// says about the ones it doesn't, and how the terse header strip that precedes it copes with a narrow
/// terminal. Pure — no app, no window — for the reason <see cref="QuitPromptTests"/> is: the part of a
/// surface that decides what it says is exactly the part a headless test can check outright.
/// <para>
/// What these do <em>not</em> claim is that pressing a key does what the row says: that is a claim about
/// the app, and it is driven key by key in <see cref="PrefixWhichKeyTests"/>. A table checked only
/// against itself would pass while every key was dead, which is the state the prefix was reported in.
/// </para>
/// </summary>
public class PrefixPanelTests
{
    /// <summary>A workspace where everything is possible: two panes, and a focused pane holding two tabs.</summary>
    private static readonly PrefixFacts Roomy =
        new(PaneCount: 2, TabCount: 2, ActiveWindowIsMain: false, Zoomed: false, RailCollapsed: false,
            SecondBarShown: false);

    private static PrefixEntry Row(PrefixFacts facts, string keys) =>
        PrefixPanel.Entries(facts).Single(e => e.Keys.StartsWith(keys, StringComparison.Ordinal));

    // --- what the panel calls live ---------------------------------------------------------------

    /// <summary>
    /// On a fresh client — one pane holding one window — five of the nine commands can do nothing, and the
    /// panel says which and why. This is the state the prefix was reported broken in: every key on the
    /// strip was a legitimate no-op, and the strip listed them all as though they were not.
    /// </summary>
    [Test]
    [Arguments("|", "needs a second tab")]
    [Arguments("-", "needs a second tab")]
    [Arguments("z", "needs a second pane")]
    [Arguments("o", "needs a second pane")]
    [Arguments("x", "the main window stays")]
    [Arguments("< >", "needs a second tab")]
    public async Task OnAFreshClientTheseAreDimmedWithTheirReason(string keys, string reason)
    {
        var row = Row(PrefixFacts.Fresh, keys);

        await Assert.That(row.Available).IsFalse();
        await Assert.That(row.Blocked).IsEqualTo(reason);
    }

    /// <summary>The three that change the client rather than the split tree are always live.</summary>
    [Test]
    [Arguments("b")]
    [Arguments("m")]
    [Arguments("i")]
    public async Task TheseAreLiveEvenOnAFreshClient(string keys)
    {
        await Assert.That(Row(PrefixFacts.Fresh, keys).Available).IsTrue();
    }

    /// <summary>Give the workspace a second pane and a second tab and nothing is dimmed at all.</summary>
    [Test]
    public async Task AWorkspaceWithRoomLeavesEveryCommandLive()
    {
        foreach (var entry in PrefixPanel.Entries(Roomy))
        {
            await Assert.That(entry.Available)
                .IsTrue()
                .Because($"'{entry.Title}' can run on a two-pane, two-tab workspace");
        }
    }

    // --- what it says ----------------------------------------------------------------------------

    /// <summary>
    /// The three toggles say which way they will go. A row reading "zoom this pane" over an already-zoomed
    /// pane would be the same defect as naming a key that does something else, one step smaller.
    /// </summary>
    [Test]
    public async Task TheTogglesNameTheDirectionTheyWouldMoveIn()
    {
        var resting = PrefixPanel.Entries(PrefixFacts.Fresh);
        await Assert.That(resting.Single(e => e.Keys == "z").Title).Contains("zoom");
        await Assert.That(resting.Single(e => e.Keys == "b").Title).Contains("hide");
        await Assert.That(resting.Single(e => e.Keys == "i").Title).Contains("show");

        var flipped = PrefixPanel.Entries(
            PrefixFacts.Fresh with { Zoomed = true, RailCollapsed = true, SecondBarShown = true });
        await Assert.That(flipped.Single(e => e.Keys == "z").Title).Contains("unzoom");
        await Assert.That(flipped.Single(e => e.Keys == "b").Title).Contains("show");
        await Assert.That(flipped.Single(e => e.Keys == "i").Title).Contains("hide");
    }

    /// <summary>A dimmed row carries its reason into the rendered markup, not only into the model.</summary>
    [Test]
    public async Task ARenderedDimmedRowCarriesItsReason()
    {
        var zoom = PrefixPanel.Render(PrefixFacts.Fresh).Single(l => l.Contains("zoom this pane"));

        await Assert.That(zoom).Contains("needs a second pane");
        await Assert.That(zoom).Contains(ScreenPalette.Label); // dimmed, not drawn as an offer
        await Assert.That(zoom).DoesNotContain(ScreenPalette.Accent);
    }

    /// <summary>And a live one is drawn as an offer, with no reason attached to it.</summary>
    [Test]
    public async Task ARenderedLiveRowIsDrawnAsAnOffer()
    {
        var move = PrefixPanel.Render(PrefixFacts.Fresh).Single(l => l.Contains("move this window"));

        await Assert.That(move).Contains(ScreenPalette.Accent);
        await Assert.That(move).DoesNotContain("—");
    }

    /// <summary>
    /// The way out is named — the defect that made this prefix the one mode with no advertised exit. Both
    /// spellings are there: Esc, and the chord that armed it.
    /// </summary>
    [Test]
    public async Task TheFooterNamesBothWaysOut()
    {
        var footer = PrefixPanel.Render(PrefixFacts.Fresh)[^1];

        await Assert.That(footer).Contains("Esc");
        await Assert.That(footer).Contains("⌃B");
    }

    /// <summary>
    /// The panel is never narrower than the line naming the way out, so that line cannot wrap. Checked
    /// against the hint itself rather than against a remembered number.
    /// </summary>
    [Test]
    public async Task TheMinimumWidthHoldsTheExitHintOnOneRow()
    {
        // + 1 for the row's left margin, + 2 for the border, + 1 so the text does not lean on the frame.
        await Assert.That(PrefixOverlay.MinimumWidth)
            .IsGreaterThanOrEqualTo(PrefixPanel.ExitHint.Length + 4);
    }

    // --- the terse strip -------------------------------------------------------------------------

    /// <summary>
    /// Given room, the strip lists the whole keymap and the exit. It is the half of which-key an expert
    /// ever sees, so dropping a key from it would make the panel the only place that key is documented.
    /// </summary>
    [Test]
    public async Task WithRoomTheStripListsEveryKeyAndTheExit()
    {
        var strip = PrefixPanel.Strip(200);

        foreach (var key in PrefixPanel.StripKeys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            await Assert.That(strip).Contains(key);
        }

        await Assert.That(strip).Contains("Esc");
    }

    /// <summary>
    /// Every spelling fits the room it was chosen for, and none of them gives up the exit. The header is
    /// one row and the framework wraps an overlong one — a wrapped header costs a row of workspace, which
    /// is a pane getting shorter because you pressed ⌃B.
    /// </summary>
    [Test]
    [Arguments(200)]
    [Arguments(60)]
    [Arguments(40)]
    [Arguments(30)]
    public async Task EverySpellingFitsItsRoomAndStillNamesTheExit(int room)
    {
        var strip = PrefixPanel.Strip(room);

        await Assert.That(VisibleLength(strip)).IsLessThanOrEqualTo(room);
        await Assert.That(strip).Contains("Esc");
    }

    /// <summary>
    /// Below the shortest spelling the strip stops shrinking rather than vanishing — a terminal that narrow
    /// still gets the panel a moment later, and a header saying nothing at all would be worse than one that
    /// slightly overhangs.
    /// </summary>
    [Test]
    public async Task BelowTheShortestSpellingItStopsShrinkingRatherThanDisappearing()
    {
        await Assert.That(PrefixPanel.Strip(1)).Contains("Esc");
    }

}
