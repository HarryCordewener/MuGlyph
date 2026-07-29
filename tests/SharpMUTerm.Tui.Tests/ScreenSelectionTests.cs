using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class ScreenSelectionTests
{
    private static int[] Sizes(params int[] sizes) => sizes;

    [Test]
    public async Task New_StartsOnFirstPaneFirstRow()
    {
        var selection = new ScreenSelection(3);

        await Assert.That(selection.PaneCount).IsEqualTo(3);
        await Assert.That(selection.Pane).IsEqualTo(0);
        await Assert.That(selection.Index).IsEqualTo(0);
    }

    /// <summary>
    /// A screen can be opened focused on a pane other than the first — F9 opens F5 in the character
    /// pane, which is where the log it used to edit now lives. Seeded cursors are left alone: the pane
    /// the key means is a different question from the row the app resumed on.
    /// </summary>
    [Test]
    public async Task FocusPane_StartsTheKeyboardInAnotherPaneWithoutMovingAnyCursor()
    {
        var selection = new ScreenSelection(3);
        selection.Seed(1, 2);

        selection.FocusPane(1);

        await Assert.That(selection.Pane).IsEqualTo(1);
        await Assert.That(selection.Index).IsEqualTo(2);
        await Assert.That(selection.CursorIn(0)).IsEqualTo(0);
    }

    [Test]
    public async Task FocusPane_IgnoresAPaneTheScreenHasNot()
    {
        var selection = new ScreenSelection(2);

        selection.FocusPane(7);
        selection.FocusPane(-1);

        await Assert.That(selection.Pane).IsEqualTo(0);
    }

    /// <summary>
    /// A focus seeded onto a pane that turns out to be empty is corrected by the first clamp, so the
    /// screen can ask for a pane without knowing whether the config filled it.
    /// </summary>
    [Test]
    public async Task FocusPane_OnAnEmptyPaneIsCorrectedByTheFirstClamp()
    {
        var selection = new ScreenSelection(3);

        selection.FocusPane(1);
        selection.Clamp(Sizes(2, 0, 1));

        await Assert.That(selection.Pane).IsEqualTo(2);
    }

    [Test]
    public async Task Move_StepsWithinThePane()
    {
        var selection = new ScreenSelection(1);

        await Assert.That(selection.Move(1, Sizes(3))).IsTrue();
        await Assert.That(selection.Index).IsEqualTo(1);
        await Assert.That(selection.Move(1, Sizes(3))).IsTrue();
        await Assert.That(selection.Index).IsEqualTo(2);
    }

    [Test]
    public async Task Move_ClampsAtBothEndsWithoutWrapping()
    {
        var selection = new ScreenSelection(1);

        await Assert.That(selection.Move(-1, Sizes(3))).IsFalse();
        await Assert.That(selection.Index).IsEqualTo(0);

        selection.Move(1, Sizes(3));
        selection.Move(1, Sizes(3));
        await Assert.That(selection.Move(1, Sizes(3))).IsFalse();
        await Assert.That(selection.Index).IsEqualTo(2);
    }

    [Test]
    public async Task Move_OnAnEmptyPaneDoesNothing()
    {
        var selection = new ScreenSelection(1);

        await Assert.That(selection.Move(1, Sizes(0))).IsFalse();
        await Assert.That(selection.Index).IsEqualTo(0);
        await Assert.That(selection.HasSelection(Sizes(0))).IsFalse();
    }

    [Test]
    public async Task NextPane_MovesForwardAndWrapsBackToTheFirst()
    {
        var selection = new ScreenSelection(3);

        await Assert.That(selection.NextPane(Sizes(2, 2, 2))).IsTrue();
        await Assert.That(selection.Pane).IsEqualTo(1);
        selection.NextPane(Sizes(2, 2, 2));
        await Assert.That(selection.Pane).IsEqualTo(2);
        selection.NextPane(Sizes(2, 2, 2));
        await Assert.That(selection.Pane).IsEqualTo(0);
    }

    [Test]
    public async Task NextPane_SkipsEmptyPanes()
    {
        var selection = new ScreenSelection(3);

        // Pane 1 has no rows (a world with no characters), so ⇥ lands on pane 2.
        await Assert.That(selection.NextPane(Sizes(2, 0, 4))).IsTrue();
        await Assert.That(selection.Pane).IsEqualTo(2);
    }

    [Test]
    public async Task NextPane_ReturnsFalseWhenNoOtherPaneHasRows()
    {
        var selection = new ScreenSelection(3);

        await Assert.That(selection.NextPane(Sizes(2, 0, 0))).IsFalse();
        await Assert.That(selection.Pane).IsEqualTo(0);
    }

    [Test]
    public async Task PreviousPane_MovesBackwardAndWraps()
    {
        var selection = new ScreenSelection(3);

        await Assert.That(selection.PreviousPane(Sizes(1, 1, 1))).IsTrue();
        await Assert.That(selection.Pane).IsEqualTo(2);
        selection.PreviousPane(Sizes(1, 1, 1));
        await Assert.That(selection.Pane).IsEqualTo(1);
    }

    [Test]
    public async Task EachPaneKeepsItsOwnCursorAcrossPaneSwitches()
    {
        var selection = new ScreenSelection(2);

        selection.Move(1, Sizes(5, 5));
        selection.Move(1, Sizes(5, 5));
        selection.NextPane(Sizes(5, 5));
        selection.Move(1, Sizes(5, 5));

        await Assert.That(selection.Index).IsEqualTo(1);
        await Assert.That(selection.CursorIn(0)).IsEqualTo(2);

        selection.PreviousPane(Sizes(5, 5));
        await Assert.That(selection.Index).IsEqualTo(2);
    }

    [Test]
    public async Task Seed_PlacesACursorWithoutMovingFocus()
    {
        var selection = new ScreenSelection(2);
        selection.Seed(1, 3);

        await Assert.That(selection.Pane).IsEqualTo(0);
        await Assert.That(selection.CursorIn(1)).IsEqualTo(3);
    }

    [Test]
    public async Task Seed_IgnoresPanesAndIndexesOutOfRange()
    {
        var selection = new ScreenSelection(2);
        selection.Seed(9, 1);
        selection.Seed(0, -1);

        await Assert.That(selection.CursorIn(0)).IsEqualTo(0);
        await Assert.That(selection.CursorIn(9)).IsEqualTo(-1);
    }

    [Test]
    public async Task Clamp_PullsACursorBackWhenItsListShrinks()
    {
        var selection = new ScreenSelection(1);
        selection.Seed(0, 7);

        selection.Clamp(Sizes(3));

        await Assert.That(selection.Index).IsEqualTo(2);
    }

    [Test]
    public async Task Clamp_MovesFocusOffAPaneThatHasEmptied()
    {
        var selection = new ScreenSelection(2);
        selection.NextPane(Sizes(2, 2));
        await Assert.That(selection.Pane).IsEqualTo(1);

        // The character list emptied under the cursor: focus falls back to the pane that still has rows.
        selection.Clamp(Sizes(2, 0));

        await Assert.That(selection.Pane).IsEqualTo(0);
    }

    [Test]
    public async Task Clamp_LeavesFocusAloneWhenNoPaneHasRows()
    {
        var selection = new ScreenSelection(2);

        selection.Clamp(Sizes(0, 0));

        await Assert.That(selection.Pane).IsEqualTo(0);
        await Assert.That(selection.HasSelection(Sizes(0, 0))).IsFalse();
    }

    [Test]
    public void Constructor_RejectsAScreenWithNoPanes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenSelection(0));
    }
}
