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
