using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The command line's geometry, which is what two of the maintainer's reports were about: it did not
/// wrap ("only goes horizontal") and it did not grow. Both are arithmetic, so both are asserted here
/// rather than inferred from a rendered frame.
/// </summary>
public class InputLayoutTests
{
    private static string RowText(string text, InputRow row) => text.Substring(row.Start, row.Length);

    [Test]
    public async Task ShortText_IsOneRow()
    {
        var rows = InputLayout.Wrap("say hello", 40);

        await Assert.That(rows).HasSingleItem();
        await Assert.That(RowText("say hello", rows[0])).IsEqualTo("say hello");
    }

    /// <summary>
    /// The headline: text longer than the field wraps onto the next row instead of scrolling sideways.
    /// The break lands after a space, so the second row starts on a word.
    /// </summary>
    [Test]
    public async Task LongText_WrapsAtTheLastSpaceThatFits()
    {
        const string text = "the quick brown fox jumps over the lazy dog";
        var rows = InputLayout.Wrap(text, 20);

        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(RowText(text, rows[0])).IsEqualTo("the quick brown fox ");
        await Assert.That(RowText(text, rows[1])).IsEqualTo("jumps over the lazy ");
        await Assert.That(RowText(text, rows[2])).IsEqualTo("dog");
    }

    /// <summary>The rows partition the text exactly — that is what makes the caret mapping reversible.</summary>
    [Test]
    public async Task Rows_CoverEveryCharacterExactlyOnce()
    {
        const string text = "a longish line with several words in it, and a second clause too";
        var rows = InputLayout.Wrap(text, 17);

        await Assert.That(string.Concat(rows.Select(r => RowText(text, r)))).IsEqualTo(text);
    }

    /// <summary>A word with no space to break on is cut at the edge rather than overflowing the row.</summary>
    [Test]
    public async Task AWordLongerThanTheRow_BreaksMidWord()
    {
        const string text = "supercalifragilistic";
        var rows = InputLayout.Wrap(text, 8);

        await Assert.That(RowText(text, rows[0])).IsEqualTo("supercal");
        await Assert.That(RowText(text, rows[1])).IsEqualTo("ifragili");
    }

    [Test]
    public async Task ANewline_BreaksHard()
    {
        const string text = "ic line\nooc line";
        var rows = InputLayout.Wrap(text, 40);

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(RowText(text, rows[0])).IsEqualTo("ic line");
        await Assert.That(RowText(text, rows[1])).IsEqualTo("ooc line");
    }

    /// <summary>
    /// A row filled to the edge is followed by an empty one: that row is where the next character goes,
    /// and the caret has to be somewhere until it is typed.
    /// </summary>
    [Test]
    public async Task TextThatFillsTheRowExactly_GetsARowForTheCaret()
    {
        var rows = InputLayout.Wrap("abcdefgh", 8);

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[1].Length).IsEqualTo(0);
        await Assert.That(InputLayout.Caret(rows, 8)).IsEqualTo((1, 0));
    }

    [Test]
    public async Task Caret_MapsToItsRowAndColumn()
    {
        const string text = "the quick brown fox jumps";
        var rows = InputLayout.Wrap(text, 20);

        await Assert.That(InputLayout.Caret(rows, 0)).IsEqualTo((0, 0));
        await Assert.That(InputLayout.Caret(rows, 5)).IsEqualTo((0, 5));
        await Assert.That(InputLayout.Caret(rows, 22)).IsEqualTo((1, 2));
    }

    [Test]
    public async Task Offset_IsTheInverseOfCaret_AndClampsToAShorterRow()
    {
        const string text = "the quick brown fox jumps";
        var rows = InputLayout.Wrap(text, 20);

        await Assert.That(InputLayout.Offset(rows, 1, 2)).IsEqualTo(22);
        await Assert.That(InputLayout.Offset(rows, 1, 99)).IsEqualTo(text.Length);
    }

    /// <summary>
    /// An empty command line is still the configured height — three lines by default, which is what
    /// the maintainer asked for — and one row of text does not change that.
    /// </summary>
    [Test]
    public async Task Height_HoldsTheFloorUntilTheTextOutgrowsIt()
    {
        await Assert.That(InputLayout.Height(0, 3, 8)).IsEqualTo(3);
        await Assert.That(InputLayout.Height(1, 3, 8)).IsEqualTo(3);
        await Assert.That(InputLayout.Height(3, 3, 8)).IsEqualTo(3);
    }

    [Test]
    public async Task Height_GrowsWithTheTextUpToTheCap()
    {
        await Assert.That(InputLayout.Height(4, 3, 8)).IsEqualTo(4);
        await Assert.That(InputLayout.Height(8, 3, 8)).IsEqualTo(8);
        await Assert.That(InputLayout.Height(40, 3, 8)).IsEqualTo(8);
    }

    /// <summary>
    /// A hand-edited configuration is surprising, not broken: both bounds are pulled into the range the
    /// control can paint, and a maximum below the minimum is raised to it rather than inverting.
    /// </summary>
    [Test]
    public async Task Height_ClampsBoundsThatTheControlCannotHonour()
    {
        await Assert.That(InputLayout.Height(1, 0, 8)).IsEqualTo(InputSettings.MinRows);
        await Assert.That(InputLayout.Height(50, 3, 500)).IsEqualTo(InputSettings.MaxRowCeiling);
        await Assert.That(InputLayout.Height(9, 6, 2)).IsEqualTo(6);
    }

    /// <summary>A line of chrome takes a second row as soon as it outgrows the terminal's width.</summary>
    [Test]
    public async Task WrappedRows_CountsTheRowsALineOfChromeReallyTakes()
    {
        await Assert.That(InputLayout.WrappedRows(80, 120)).IsEqualTo(1);
        await Assert.That(InputLayout.WrappedRows(120, 120)).IsEqualTo(1);
        await Assert.That(InputLayout.WrappedRows(121, 120)).IsEqualTo(2);
        await Assert.That(InputLayout.WrappedRows(0, 120)).IsEqualTo(1);   // an empty status line is still a row
        await Assert.That(InputLayout.WrappedRows(40, 0)).IsEqualTo(1);    // and a width we don't know yet
    }

    /// <summary>
    /// The veto: the output keeps at least as many rows as the input area, whatever the configuration
    /// asks for, and the share is taken from what the header and the status line leave rather than from
    /// the whole terminal — the rows they occupy are reserved before the workspace is measured at all.
    /// </summary>
    [Test]
    public async Task Room_LeavesTheOutputAtLeastAsManyRowsAsTheBars()
    {
        // A roomy terminal: the configured heights (3, growing to 8) are nowhere near the cap.
        await Assert.That(InputLayout.Room(windowRows: 34, chromeRows: 2, bars: 1)).IsEqualTo(16);
        await Assert.That(InputLayout.Room(windowRows: 34, chromeRows: 2, bars: 2)).IsEqualTo(8);

        // A cramped one: two bars at their configured eight rows would be the whole window.
        await Assert.That(InputLayout.Room(windowRows: 12, chromeRows: 2, bars: 2)).IsEqualTo(2);

        // And one where the chrome is what does not fit — an 80-column header and status line both
        // wrap, which is the case that used to leave no output row at all.
        await Assert.That(InputLayout.Room(windowRows: 6, chromeRows: 4, bars: 1)).IsEqualTo(1);
        await Assert.That(InputLayout.Room(windowRows: 6, chromeRows: 8, bars: 1))
            .IsEqualTo(InputSettings.MinRows);
    }

    /// <summary>Text that fits is never scrolled, so a shrinking line cannot keep a stale offset.</summary>
    [Test]
    public async Task Scroll_IsZeroWhileEverythingFits()
    {
        await Assert.That(InputLayout.Scroll(caretRow: 2, rows: 3, height: 8, scroll: 4)).IsEqualTo(0);
    }

    [Test]
    public async Task Scroll_FollowsTheCaretPastTheCap()
    {
        // 20 rows in an 8-tall bar: a caret on the last row puts rows 12..19 on screen.
        await Assert.That(InputLayout.Scroll(caretRow: 19, rows: 20, height: 8, scroll: 0)).IsEqualTo(12);

        // And back up: a caret above the window pulls it to the caret's own row.
        await Assert.That(InputLayout.Scroll(caretRow: 3, rows: 20, height: 8, scroll: 12)).IsEqualTo(3);

        // A caret already on screen leaves the offset where it was.
        await Assert.That(InputLayout.Scroll(caretRow: 14, rows: 20, height: 8, scroll: 12)).IsEqualTo(12);
    }
}
