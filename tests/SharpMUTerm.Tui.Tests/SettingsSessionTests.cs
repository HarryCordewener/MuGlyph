using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class SettingsSessionTests
{

    /// <summary>
    /// Where the cursor is, and whether a field is open on it — the whole of what a navigation assertion
    /// is about. Compared as a tuple rather than as a whole <see cref="ScreenFocus"/> because the focus
    /// also carries a <em>derived</em> reading of what ⏎ would do on the row
    /// (<see cref="ScreenEnter"/>, for the action bar's chip), and a movement test restating that would be
    /// pinning a label from the wrong place.
    /// </summary>
    private static (int Pane, int Index, bool Editing) Cursor(SettingsSession session)
    {
        var focus = session.Focus();
        return (focus.Pane, focus.Index, focus.IsEditing);
    }
    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = default) =>
        new('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift), false, false);

    /// <summary>A two-pane screen: a three-row list of checkboxes over a two-row editor.</summary>
    private sealed class Scene
    {
        public bool[] List { get; } = new bool[3];

        public bool[] Editor { get; } = new bool[2];

        public SettingsSession Session() => new(_ => new ScreenModel(
            ScreenModel.Toggles(
                new[] { 0, 1, 2 }, i => List[i], (i, v) => List[i] = v),
            ScreenModel.Toggles(
                new[] { 0, 1 }, i => Editor[i], (i, v) => Editor[i] = v)));
    }

    [Test]
    public async Task PaneCount_ComesFromTheModel()
    {
        var session = new Scene().Session();

        await Assert.That(session.Selection.PaneCount).IsEqualTo(2);
    }

    [Test]
    public async Task DownArrow_MovesTheCursorAndAsksForARedraw()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.DownArrow))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(Cursor(session)).IsEqualTo((0, 1, false));
    }

    [Test]
    public async Task UpArrow_AtTheTopIsSwallowedWithoutARedraw()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.UpArrow))).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(Cursor(session)).IsEqualTo((0, 0, false));
    }

    [Test]
    public async Task Tab_MovesToTheEditorPane_AndShiftTabComesBack()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.Tab))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(Cursor(session)).IsEqualTo((1, 0, false));

        session.Handle(Key(ConsoleKey.Tab, ConsoleModifiers.Shift));
        await Assert.That(Cursor(session)).IsEqualTo((0, 0, false));
    }

    [Test]
    public async Task Space_FlipsTheCheckboxUnderTheCursor()
    {
        var scene = new Scene();
        var session = scene.Session();

        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(session.Handle(Key(ConsoleKey.Spacebar))).IsEqualTo(ScreenAction.Redraw);

        await Assert.That(scene.List[1]).IsTrue();
        await Assert.That(scene.List[0]).IsFalse();

        // A flipped checkbox is committed on the spot and raises nothing for the closing review: it is
        // kept whatever the user does next. This replaced an IsDirty assertion, from when Esc would have
        // put it back.
        await Assert.That(session.Edits.HasDeletions).IsFalse();
    }

    [Test]
    public async Task Space_OnARowWithNoCheckboxIsSwallowedAndChangesNothing()
    {
        var session = new SettingsSession(_ => new ScreenModel(ScreenModel.Stops(2)));

        await Assert.That(session.Handle(Key(ConsoleKey.Spacebar))).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(session.Edits.HasDeletions).IsFalse();
    }

    /// <summary>
    /// Both keys close the screen, and neither discards anything. ⏎ used to mean <c>Save</c> — a distinct
    /// action that committed the screen's undo log before closing — and Esc used to mean <c>Cancel</c>,
    /// which replayed it. There is nothing left for the two to differ about: every committed change is
    /// already in config and on disk, so the two keys are one answer under two names.
    /// </summary>
    [Test]
    public async Task EnterOnAPlainRowAndEscapeBothClose()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.Enter))).IsEqualTo(ScreenAction.Close);
        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Close);
    }

    /// <summary>
    /// And ⌃S is gone. It meant "commit and close" on a screen that could otherwise discard; with every
    /// change already written the moment it is committed, a save chord would be claiming work that is
    /// finished — and one that quietly closed the screen instead would be a worse answer than none. The
    /// key is left for the framework.
    /// </summary>
    [Test]
    public async Task ThereIsNoSaveChord()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.S, ConsoleModifiers.Control)))
            .IsEqualTo(ScreenAction.None);
    }

    [Test]
    public async Task AnUnrelatedKeyIsLeftForTheFramework()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.F1))).IsEqualTo(ScreenAction.None);
    }

    /// <summary>
    /// <b>Bug: "when I change the address of a world, it does not stick."</b> Whatever the screen does on
    /// the way out, the checkboxes the user pressed stay pressed. This test asserted the exact opposite
    /// until now — that Revert undid every toggle the screen had applied — and Esc, the F-key and ⌃Q's
    /// prompt all ran that revert while the header called the keys <c>close</c>.
    /// </summary>
    [Test]
    public async Task ClosingTheScreenKeepsEveryToggleItApplied()
    {
        var scene = new Scene();
        scene.List[0] = true;
        var session = scene.Session();

        session.Handle(Key(ConsoleKey.Spacebar));
        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.Spacebar));
        await Assert.That(scene.List[0]).IsFalse();
        await Assert.That(scene.Editor[0]).IsTrue();

        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Close);
        await Assert.That(session.Edits.HasDeletions).IsFalse();
        session.Edits.Revert(); // the review's "put them back", which has nothing to put back

        await Assert.That(scene.List[0]).IsFalse();
        await Assert.That(scene.Editor[0]).IsTrue();
    }

    [Test]
    public async Task Focus_IsNoneWhenTheScreenHasNoRowsAtAll()
    {
        var session = new SettingsSession(_ => new ScreenModel(Array.Empty<ScreenRow>()));

        await Assert.That(session.Focus()).IsEqualTo(ScreenFocus.None);
    }

    [Test]
    public async Task Focus_FollowsAPaneWhoseRowsAppearAfterTheCursorMoves()
    {
        // The second pane only exists once the first pane's cursor is off row 0 — the shape F5 has,
        // where a world with no characters offers nothing to ⇥ into.
        var selectionSizes = new[] { 2, 0 };
        var session = new SettingsSession(selection => new ScreenModel(
            ScreenModel.Stops(selectionSizes[0]),
            ScreenModel.Stops(selection.CursorIn(0) == 1 ? 3 : 0)));

        await Assert.That(session.Handle(Key(ConsoleKey.Tab))).IsEqualTo(ScreenAction.Consumed);

        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(session.Handle(Key(ConsoleKey.Tab))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(Cursor(session)).IsEqualTo((1, 0, false));
    }
}
