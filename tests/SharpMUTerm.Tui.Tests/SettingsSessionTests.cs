using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class SettingsSessionTests
{
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
        await Assert.That(session.Focus()).IsEqualTo(new ScreenFocus(0, 1));
    }

    [Test]
    public async Task UpArrow_AtTheTopIsSwallowedWithoutARedraw()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.UpArrow))).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(session.Focus()).IsEqualTo(new ScreenFocus(0, 0));
    }

    [Test]
    public async Task Tab_MovesToTheEditorPane_AndShiftTabComesBack()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.Tab))).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(session.Focus()).IsEqualTo(new ScreenFocus(1, 0));

        session.Handle(Key(ConsoleKey.Tab, ConsoleModifiers.Shift));
        await Assert.That(session.Focus()).IsEqualTo(new ScreenFocus(0, 0));
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
        await Assert.That(session.Edits.IsDirty).IsTrue();
    }

    [Test]
    public async Task Space_OnARowWithNoCheckboxIsSwallowedAndChangesNothing()
    {
        var session = new SettingsSession(_ => new ScreenModel(ScreenModel.Stops(2)));

        await Assert.That(session.Handle(Key(ConsoleKey.Spacebar))).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(session.Edits.IsDirty).IsFalse();
    }

    [Test]
    public async Task Enter_Saves_AndEscape_Cancels()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.Enter))).IsEqualTo(ScreenAction.Save);
        await Assert.That(session.Handle(Key(ConsoleKey.Escape))).IsEqualTo(ScreenAction.Cancel);
    }

    [Test]
    public async Task AnUnrelatedKeyIsLeftForTheFramework()
    {
        var session = new Scene().Session();

        await Assert.That(session.Handle(Key(ConsoleKey.F1))).IsEqualTo(ScreenAction.None);
    }

    [Test]
    public async Task RevertingTheEditsUndoesEveryToggleTheScreenApplied()
    {
        var scene = new Scene();
        scene.List[0] = true;
        var session = scene.Session();

        session.Handle(Key(ConsoleKey.Spacebar));
        session.Handle(Key(ConsoleKey.Tab));
        session.Handle(Key(ConsoleKey.Spacebar));
        await Assert.That(scene.List[0]).IsFalse();
        await Assert.That(scene.Editor[0]).IsTrue();

        session.Edits.Revert();

        await Assert.That(scene.List[0]).IsTrue();
        await Assert.That(scene.Editor[0]).IsFalse();
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
        await Assert.That(session.Focus()).IsEqualTo(new ScreenFocus(1, 0));
    }
}
