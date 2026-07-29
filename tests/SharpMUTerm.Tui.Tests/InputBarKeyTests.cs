using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The command line's key table, driven through the very method the framework calls. The pair that
/// matters most is ⏎ against the newline chord: one sends and the other must not, and a client that
/// got those the wrong way round would send half a pose every time somebody reached for a second line.
/// </summary>
public class InputBarKeyTests
{
    private static InputBarControl Bar()
    {
        var bar = new InputBarControl { Width = 60 };
        return bar;
    }

    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static ConsoleKeyInfo Chord(ConsoleKey key, bool ctrl = false, bool shift = false) =>
        new('\0', key, shift, false, ctrl);

    private static void Type(InputBarControl bar, string text)
    {
        foreach (var c in text)
        {
            bar.ProcessKey(Key(c));
        }
    }

    [Test]
    public async Task Enter_SendsTheLineAndEmptiesTheBar()
    {
        var bar = Bar();
        string? sent = null;
        bar.Entered += text => sent = text;

        Type(bar, "look");
        bar.ProcessKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        await Assert.That(sent).IsEqualTo("look");
        await Assert.That(bar.Text).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// Ctrl+L is the newline chord this host can actually deliver, and it must not send. Shift+⏎ and
    /// Ctrl+⏎ do the same where a host reports them (Windows' <c>Console.ReadKey</c> does; the Unix
    /// parser cannot, having no CSI-u) — all three are asserted as "no send, one more line".
    /// </summary>
    [Test]
    [Arguments(ConsoleKey.L, true, false)]
    [Arguments(ConsoleKey.Enter, true, false)]
    [Arguments(ConsoleKey.Enter, false, true)]
    public async Task TheNewlineChords_BreakTheLineAndSendNothing(ConsoleKey key, bool ctrl, bool shift)
    {
        var bar = Bar();
        var sends = 0;
        bar.Entered += _ => sends++;

        Type(bar, "first");
        bar.ProcessKey(Chord(key, ctrl, shift));
        Type(bar, "second");

        await Assert.That(sends).IsEqualTo(0);
        await Assert.That(bar.Text).IsEqualTo("first\nsecond");
    }

    /// <summary>
    /// And the whole multiline line goes out on one ⏎ — the newlines are the payload, not a reason to
    /// send twice.
    /// </summary>
    [Test]
    public async Task AMultilineDraft_IsSentWholeOnOneEnter()
    {
        var bar = Bar();
        string? sent = null;
        bar.Entered += text => sent = text;

        Type(bar, "pose ");
        bar.ProcessKey(Chord(ConsoleKey.L, ctrl: true));
        Type(bar, "smiles.");
        bar.ProcessKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        await Assert.That(sent).IsEqualTo("pose \nsmiles.");
    }

    [Test]
    public async Task Typing_RaisesTheChangeThatRecordsTheDraft()
    {
        var bar = Bar();
        var last = string.Empty;
        bar.Changed += text => last = text;

        Type(bar, "say hi");

        await Assert.That(last).IsEqualTo("say hi");
    }

    /// <summary>A caret move is not an edit: it must not re-record a draft or disturb history recall.</summary>
    [Test]
    public async Task MovingTheCaret_RaisesNoChange()
    {
        var bar = Bar();
        Type(bar, "say hi");
        var changes = 0;
        bar.Changed += _ => changes++;

        bar.ProcessKey(Chord(ConsoleKey.LeftArrow));
        bar.ProcessKey(Chord(ConsoleKey.Home));
        bar.ProcessKey(Chord(ConsoleKey.End));

        await Assert.That(changes).IsEqualTo(0);
    }

    /// <summary>
    /// The bar grows as its text wraps, from the configured floor to the configured cap, and stops
    /// there — past the cap it scrolls inside itself instead of eating the output window.
    /// </summary>
    [Test]
    public async Task TheBar_GrowsWithWhatIsTypedAndStopsAtItsCap()
    {
        var bar = new InputBarControl { Width = 20, MinRows = 3, MaxRows = 5 };

        await Assert.That(bar.Rows()).IsEqualTo(3);

        Type(bar, new string('x', 60)); // 3 rows of 20
        await Assert.That(bar.Rows()).IsEqualTo(4); // 3 full rows + the caret's own

        Type(bar, new string('y', 200));
        await Assert.That(bar.Rows()).IsEqualTo(5);
    }

    /// <summary>Alt chords belong to the app's bindings; typing their letter into the line is not the answer.</summary>
    [Test]
    public async Task AnAltChord_IsNotTyping()
    {
        var bar = Bar();

        await Assert.That(bar.ProcessKey(new ConsoleKeyInfo('n', ConsoleKey.N, false, true, false))).IsFalse();
        await Assert.That(bar.Text).IsEqualTo(string.Empty);
    }

    /// <summary>⇥ only belongs to the bar when there is another bar to hand the caret to.</summary>
    [Test]
    public async Task Tab_IsOnlyTheBarsWhenASecondBarIsUp()
    {
        var bar = Bar();

        await Assert.That(bar.WantsTabKey).IsFalse();
        await Assert.That(bar.ProcessKey(Chord(ConsoleKey.Tab))).IsFalse();

        var cycles = 0;
        bar.HasSibling = () => true;
        bar.CycleRequested += () => cycles++;

        await Assert.That(bar.WantsTabKey).IsTrue();
        await Assert.That(bar.ProcessKey(Chord(ConsoleKey.Tab))).IsTrue();
        await Assert.That(cycles).IsEqualTo(1);
    }

    /// <summary>A pasted paragraph keeps its line breaks — this bar has rows for them.</summary>
    [Test]
    public async Task Paste_KeepsNewlines()
    {
        var bar = Bar();

        bar.Paste("one\r\ntwo\rthree");

        await Assert.That(bar.Text).IsEqualTo("one\ntwo\nthree");
    }
}
