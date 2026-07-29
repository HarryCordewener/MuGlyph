using SharpMUTerm.Core.Input;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What the ⌃R surface means and what it says. The rules live in <see cref="HistorySearchPrompt"/> rather
/// than in the overlay for the reason <see cref="QuitPromptTests"/> exists: a modal's keyboard is exactly
/// the part a headless test can pin, and the host is left with nothing but framework calls.
/// </summary>
public class HistorySearchPromptTests
{
    private static readonly string[] Entries =
    {
        "look", "say hello", "north", "say the northern watch sent word", "score",
    };

    private static IReadOnlyList<HistoryMatch> Matches(string query = "") =>
        HistorySearch.Match(Entries, query);

    private static ConsoleKeyInfo Bare(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Typed(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static ConsoleKeyInfo Chord(ConsoleKey key, bool ctrl = false, bool alt = false) =>
        new('\0', key, false, alt, ctrl);

    private static string Rendered(IReadOnlyList<HistoryMatch> matches, string query = "", int selected = 0) =>
        string.Join("\n", HistorySearchPrompt.Render(matches, query, selected, Entries.Length, "command line"));

    // ---- The keys the footer names, one test each ------------------------------------------------

    /// <summary>
    /// The honesty rule, in the form it can be enforced: for every key the footer advertises, the footer
    /// really names it <em>and</em> <see cref="HistorySearchPrompt.Interpret"/> really acts on it. The tests
    /// below say what each one does; this one says none of them is decoration. (⌃R has its own test because
    /// it is the only advertised key that carries a modifier.)
    /// </summary>
    [Test]
    [Arguments("type to filter", 'x', ConsoleKey.NoName)]
    [Arguments("↑↓ pick", '\0', ConsoleKey.UpArrow)]
    [Arguments("↑↓ pick", '\0', ConsoleKey.DownArrow)]
    [Arguments("⏎ insert", '\0', ConsoleKey.Enter)]
    [Arguments("Esc cancel", '\0', ConsoleKey.Escape)]
    public async Task EveryKeyTheFooterNamesDoesSomething(string hint, char c, ConsoleKey key)
    {
        await Assert.That(HistorySearchPrompt.Hints).Contains(hint);

        var decision = HistorySearchPrompt.Interpret(
            new ConsoleKeyInfo(c, key, false, false, false), "north", 0, 2);

        await Assert.That(decision.Action).IsNotEqualTo(HistoryAction.None);
    }

    /// <summary>Typing appends to the filter and puts the pointer back on the newest match.</summary>
    [Test]
    public async Task TypingFiltersFromTheTop()
    {
        var decision = HistorySearchPrompt.Interpret(Typed('n'), "sou", 3, 5);

        await Assert.That(decision.Action).IsEqualTo(HistoryAction.Redraw);
        await Assert.That(decision.Query).IsEqualTo("soun");
        await Assert.That(decision.Selected).IsEqualTo(0);
    }

    /// <summary>Space is typing, not a command — MU* lines are full of it.</summary>
    [Test]
    public async Task SpaceIsPartOfTheFilter()
    {
        await Assert.That(HistorySearchPrompt.Interpret(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false), "say", 0, 5).Query)
            .IsEqualTo("say ");
    }

    /// <summary>⌫ widens the filter; on an empty one there is nothing to widen.</summary>
    [Test]
    public async Task BackspaceWidensTheFilter()
    {
        var decision = HistorySearchPrompt.Interpret(Bare(ConsoleKey.Backspace), "north", 2, 2);

        await Assert.That(decision.Action).IsEqualTo(HistoryAction.Redraw);
        await Assert.That(decision.Query).IsEqualTo("nort");
        await Assert.That(decision.Selected).IsEqualTo(0);
    }

    [Test]
    public async Task BackspaceOnAnEmptyFilterDoesNothing()
    {
        await Assert.That(HistorySearchPrompt.Interpret(Bare(ConsoleKey.Backspace), string.Empty, 0, 5).Action)
            .IsEqualTo(HistoryAction.None);
    }

    /// <summary>↑↓ walk the rows and wrap, the way ⌃P's list does.</summary>
    [Test]
    public async Task TheArrowsWalkTheRowsAndWrap()
    {
        await Assert.That(HistorySearchPrompt.Interpret(Bare(ConsoleKey.DownArrow), "", 0, 5).Selected).IsEqualTo(1);
        await Assert.That(HistorySearchPrompt.Interpret(Bare(ConsoleKey.UpArrow), "", 1, 5).Selected).IsEqualTo(0);
        await Assert.That(HistorySearchPrompt.Interpret(Bare(ConsoleKey.UpArrow), "", 0, 5).Selected).IsEqualTo(4);
        await Assert.That(HistorySearchPrompt.Interpret(Bare(ConsoleKey.DownArrow), "", 4, 5).Selected).IsEqualTo(0);
    }

    [Test]
    public async Task EnterInserts()
    {
        await Assert.That(HistorySearchPrompt.Interpret(Bare(ConsoleKey.Enter), "", 2, 5).Action)
            .IsEqualTo(HistoryAction.Insert);
    }

    /// <summary>
    /// With nothing listed there is nothing to insert, so ⏎ leaves the surface up rather than closing it on
    /// a row that does not exist: the query is what needs fixing, and Esc is the advertised way out.
    /// </summary>
    [Test]
    public async Task EnterWithNoMatchesDoesNothing()
    {
        await Assert.That(HistorySearchPrompt.Interpret(Bare(ConsoleKey.Enter), "zzz", -1, 0).Action)
            .IsEqualTo(HistoryAction.None);
    }

    [Test]
    public async Task EscapeCancels()
    {
        await Assert.That(HistorySearchPrompt.Interpret(Bare(ConsoleKey.Escape), "north", 0, 2).Action)
            .IsEqualTo(HistoryAction.Cancel);
    }

    /// <summary>
    /// ⌃R again closes — the toggle every surface in this client is on. Stated here as well as in the
    /// overlay's own toggle so a test can read the rule back; in the running client the global shortcut
    /// answers it first, and answers it the same way.
    /// </summary>
    [Test]
    public async Task CtrlRClosesToo()
    {
        await Assert.That(HistorySearchPrompt.Hints).Contains("⌃R closes");
        await Assert.That(HistorySearchPrompt.Interpret(Chord(ConsoleKey.R, ctrl: true), "", 0, 5).Action)
            .IsEqualTo(HistoryAction.Cancel);
    }

    /// <summary>
    /// Everything else is swallowed rather than passed down. A list floating over the command line that let
    /// stray keys through would be typing into a bar the user cannot currently see.
    /// </summary>
    [Test]
    [Arguments(ConsoleKey.Tab, false, false)]
    [Arguments(ConsoleKey.LeftArrow, false, false)]
    [Arguments(ConsoleKey.F5, false, false)]
    [Arguments(ConsoleKey.W, true, false)]  // the app's close-window chord
    [Arguments(ConsoleKey.Q, true, false)]  // and its quit chord
    [Arguments(ConsoleKey.N, false, true)]  // Alt is a macro's, never the surface's
    public async Task EveryOtherKeyIsSwallowed(ConsoleKey key, bool ctrl, bool alt)
    {
        var decision = HistorySearchPrompt.Interpret(Chord(key, ctrl, alt), "north", 1, 2);

        await Assert.That(decision.Action).IsEqualTo(HistoryAction.None);
        await Assert.That(decision.Query).IsEqualTo("north");
        await Assert.That(decision.Selected).IsEqualTo(1);
    }

    // ---- What it says ---------------------------------------------------------------------------

    /// <summary>The opening state: the whole list, newest first, and the surface says so.</summary>
    [Test]
    public async Task ItOpensSayingItIsShowingEverythingNewestFirst()
    {
        var text = Rendered(Matches());

        await Assert.That(text).Contains("newest first");
        await Assert.That(text).Contains("5 entries");
        await Assert.That(text).Contains("command line");
        await Assert.That(text).Contains(HistorySearchPrompt.Hints);
    }

    /// <summary>The rows are drawn newest first, and the pointed-at one carries the accent bar.</summary>
    [Test]
    public async Task TheRowsAreDrawnNewestFirstWithThePointerOnOne()
    {
        var lines = HistorySearchPrompt.Render(Matches(), string.Empty, 0, Entries.Length, "command line");
        var rows = lines.Where(l => l.Contains("score") || l.Contains("look")).ToList();

        await Assert.That(rows[0]).Contains("score");   // newest
        await Assert.That(rows[0]).Contains("▸");       // and pointed at
        await Assert.That(rows[^1]).Contains("look");   // oldest, last
        await Assert.That(rows[^1]).DoesNotContain("▸");
    }

    /// <summary>A filtered row shows why it is in the list: the matched run is marked.</summary>
    [Test]
    public async Task AFilteredRowMarksWhereTheQueryMatched()
    {
        // Row 1, not row 0: the pointed-at row is one accent bar end to end, and a highlight inside a
        // highlight would be worse than none — so the mark is what an *unselected* match carries.
        var lines = HistorySearchPrompt.Render(Matches("north"), "north", 1, Entries.Length, "command line");
        // The row's text is no longer one run — that is the point — so it is found by its tail.
        var row = lines.Single(l => l.Contains("watch sent word"));

        await Assert.That(row).Contains("[bold #00f5b7]north[/]");
    }

    /// <summary>And the count says how far the filter narrowed it.</summary>
    [Test]
    public async Task TheCountReportsTheNarrowing()
    {
        await Assert.That(Rendered(Matches("north"), "north")).Contains("2 of 5");
    }

    [Test]
    public async Task AFilterThatMatchesNothingSaysSo()
    {
        var text = Rendered(Matches("zzz"), "zzz", -1);

        await Assert.That(text).Contains("no matches");
        await Assert.That(text).Contains("⌫ widens"); // where the key is offered, it is named
        await Assert.That(text).Contains("0 of 5");
        await Assert.That(text).Contains(HistorySearchPrompt.Hints); // still every key, still true
    }

    /// <summary>An empty history says what it is rather than drawing an empty box.</summary>
    [Test]
    public async Task AnEmptyHistorySaysThereIsNothingYet()
    {
        var lines = HistorySearchPrompt.Render(
            Array.Empty<HistoryMatch>(), string.Empty, -1, 0, "command line");

        await Assert.That(string.Join("\n", lines)).Contains("nothing has been entered");
    }

    /// <summary>The surface names the command line it belongs to — history is per bar.</summary>
    [Test]
    public async Task ItNamesWhichCommandLineItIsShowing()
    {
        var lines = HistorySearchPrompt.Render(
            Matches(), string.Empty, 0, Entries.Length, "second command line");

        await Assert.That(string.Join("\n", lines)).Contains("second command line");
    }

    /// <summary>Configured text cannot inject markup: a command containing brackets is escaped.</summary>
    [Test]
    public async Task AnEntryContainingMarkupIsEscaped()
    {
        var matches = HistorySearch.Match(new[] { "say [bold]not markup[/]" }, string.Empty);
        var lines = HistorySearchPrompt.Render(matches, string.Empty, 0, 1, "command line");

        await Assert.That(string.Join("\n", lines)).Contains("[[bold]]not markup");
    }

    /// <summary>An over-long entry is elided rather than wrapped or clipped past the frame.</summary>
    [Test]
    public async Task AnOverLongEntryIsElided()
    {
        var pose = "pose " + new string('x', 300);
        var matches = HistorySearch.Match(new[] { pose }, string.Empty);

        var lines = HistorySearchPrompt.Render(matches, string.Empty, -1, 1, "command line", width: 72);

        foreach (var line in lines)
        {
            await Assert.That(MarkupText.VisibleLength(line)).IsLessThanOrEqualTo(72);
        }

        await Assert.That(string.Join("\n", lines)).Contains("…");
    }

    /// <summary>
    /// And the elision keeps the match visible: a query found deep inside a pose must not be windowed out
    /// of the row that is in the list <em>because</em> of it.
    /// </summary>
    [Test]
    public async Task ElisionKeepsTheMatchedRunVisible()
    {
        var pose = "pose " + new string('x', 200) + " lantern";
        var matches = HistorySearch.Match(new[] { pose }, "lantern");

        var text = string.Join("\n", HistorySearchPrompt.Render(
            matches, "lantern", -1, 1, "command line", width: 72));

        await Assert.That(text).Contains("lantern");
    }

    /// <summary>
    /// The footer fits the narrowest surface. <c>HistorySurface.MinimumWidth</c> exists to guarantee it, and
    /// a footer that outgrew it would wrap onto a second row and push the list up — so the two are checked
    /// against each other rather than kept in step by hand.
    /// </summary>
    [Test]
    public async Task TheFooterFitsTheNarrowestSurface()
    {
        var lines = HistorySearchPrompt.Render(
            Matches(), string.Empty, 0, Entries.Length, "command line", HistorySurface.MinimumWidth);

        foreach (var line in lines)
        {
            await Assert.That(MarkupText.VisibleLength(line))
                .IsLessThanOrEqualTo(HistorySurface.MinimumWidth);
        }
    }

    // ---- The list viewport ----------------------------------------------------------------------

    /// <summary>
    /// The list area is drawn at full height whatever the filter leaves in it, so the footer does not walk
    /// up the screen on every keystroke.
    /// </summary>
    [Test]
    public async Task TheListAreaKeepsItsHeightWhenTheFilterNarrows()
    {
        var full = HistorySearchPrompt.Render(Matches(), "", 0, Entries.Length, "command line", 72, listRows: 5);
        var narrow = HistorySearchPrompt.Render(
            Matches("north"), "north", 0, Entries.Length, "command line", 72, listRows: 5);

        await Assert.That(narrow.Count).IsEqualTo(full.Count);
        await Assert.That(narrow[^1]).IsEqualTo(full[^1]); // the footer is on the same row
    }

    /// <summary>
    /// A history longer than the surface is walkable: the viewport follows the pointer instead of running
    /// off the bottom of the window with it.
    /// </summary>
    [Test]
    public async Task TheViewportFollowsThePointerThroughALongHistory()
    {
        var many = Enumerable.Range(0, 40).Select(i => $"command {i}").ToArray();
        var matches = HistorySearch.Match(many, string.Empty);

        // Row 30 of 40, in a ten-row list: the top has to have moved to keep it drawn.
        var first = HistorySearchPrompt.Scroll(0, 30, matches.Count, 10);
        var text = string.Join("\n", HistorySearchPrompt.Render(
            matches, string.Empty, 30, many.Length, "command line", 72, listRows: 10, first: first));

        await Assert.That(text).Contains(matches[30].Text);
        await Assert.That(text).DoesNotContain(matches[0].Text);
    }

    /// <summary>And it moves as little as it can — a list that recentred on every step would be unreadable.</summary>
    [Test]
    public async Task TheViewportMovesOnlyWhenItHasTo()
    {
        await Assert.That(HistorySearchPrompt.Scroll(0, 5, 40, 10)).IsEqualTo(0);   // already visible
        await Assert.That(HistorySearchPrompt.Scroll(0, 10, 40, 10)).IsEqualTo(1);  // one past the bottom
        await Assert.That(HistorySearchPrompt.Scroll(5, 4, 40, 10)).IsEqualTo(4);   // one above the top
        await Assert.That(HistorySearchPrompt.Scroll(0, 39, 40, 10)).IsEqualTo(30); // the last row
        await Assert.That(HistorySearchPrompt.Scroll(9, 0, 40, 10)).IsEqualTo(0);   // wrapped to the top
        await Assert.That(HistorySearchPrompt.Scroll(9, 0, 5, 10)).IsEqualTo(0);    // shorter than the list
    }

}
