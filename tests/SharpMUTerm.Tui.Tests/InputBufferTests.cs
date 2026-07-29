using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What a keystroke does to the command line's text. The buffer is where the newline lives, so the
/// multiline requirement is asserted here; the chord that reaches it is <see cref="InputBarKeyTests"/>.
/// </summary>
public class InputBufferTests
{
    [Test]
    public async Task Insert_PutsTextAtTheCaretAndMovesIt()
    {
        var buffer = new InputBuffer();
        buffer.Insert("say hello");
        buffer.MoveTo(4);
        buffer.Insert("there ");

        await Assert.That(buffer.Text).IsEqualTo("say there hello");
        await Assert.That(buffer.Caret).IsEqualTo(10);
    }

    [Test]
    public async Task InsertNewline_BreaksTheLineWithoutSendingIt()
    {
        var buffer = new InputBuffer();
        buffer.Insert("first");
        buffer.InsertNewline();
        buffer.Insert("second");

        await Assert.That(buffer.Text).IsEqualTo("first\nsecond");
        await Assert.That(buffer.IsMultiline).IsTrue();
    }

    [Test]
    public async Task Backspace_AtTheStart_ChangesNothing()
    {
        var buffer = new InputBuffer();
        buffer.Insert("hi");
        buffer.MoveTo(0);

        await Assert.That(buffer.Backspace()).IsFalse();
        await Assert.That(buffer.Text).IsEqualTo("hi");
    }

    [Test]
    public async Task KillsAndWordMoves_FollowReadlineHabits()
    {
        var buffer = new InputBuffer();
        buffer.Insert("page anvil = are you about");

        buffer.MoveWordLeft();
        await Assert.That(buffer.Caret).IsEqualTo(21);

        buffer.KillToEnd();
        await Assert.That(buffer.Text).IsEqualTo("page anvil = are you ");

        buffer.KillWordLeft();
        await Assert.That(buffer.Text).IsEqualTo("page anvil = are ");

        buffer.KillToStart();
        await Assert.That(buffer.IsEmpty).IsTrue();
    }

    /// <summary>
    /// Home and End are the visual row's, not the whole buffer's — on a wrapped line that is the
    /// difference between the start of what you are looking at and the start of the paragraph.
    /// </summary>
    [Test]
    public async Task HomeAndEnd_ActOnTheVisualRow()
    {
        var buffer = new InputBuffer();
        buffer.Insert("the quick brown fox jumps over it");
        buffer.MoveTo(24);

        buffer.MoveHome(20);
        await Assert.That(buffer.Caret).IsEqualTo(20);

        buffer.MoveEnd(20);
        await Assert.That(buffer.Caret).IsEqualTo(33);
    }

    [Test]
    public async Task MoveRow_WalksTheWrappedRowsAndStopsAtTheEnds()
    {
        var buffer = new InputBuffer();
        buffer.Insert("the quick brown fox jumps over the lazy dog");
        buffer.MoveTo(2); // row 0, column 2

        await Assert.That(buffer.MoveRow(-1, 20)).IsFalse();
        await Assert.That(buffer.MoveRow(1, 20)).IsTrue();
        await Assert.That(buffer.Caret).IsEqualTo(22);

        buffer.MoveTo(buffer.Text.Length);
        await Assert.That(buffer.MoveRow(1, 20)).IsFalse();
    }

    [Test]
    public async Task Set_ReportsOnlyARealChange()
    {
        var buffer = new InputBuffer();

        await Assert.That(buffer.Set("look")).IsTrue();
        await Assert.That(buffer.Set("look")).IsFalse();
        await Assert.That(buffer.Caret).IsEqualTo(4);
    }
}
