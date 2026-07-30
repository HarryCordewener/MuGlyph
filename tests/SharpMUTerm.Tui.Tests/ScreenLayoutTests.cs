using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// How a settings screen decides what it has room for. The panes used to be laid out against
/// constants — a 56-cell list column and a body stretched to the window — which were right at the
/// width and height they were drawn at and wrong at every other: at 100×24 F2's editor lost its three
/// checkboxes off the bottom and its attribute legend off the right, F4's binding rows lost their
/// commands, and F5's whole CHARACTERS list sat below the fold while ↑↓ walked through it. At 120 and
/// 160 the same constants left a thirty-row empty pane under four rows of rules.
/// <para>
/// The three rules below are the fix, and they are pure functions of the space available, so they are
/// pinned here rather than only through the screens that call them.
/// </para>
/// </summary>
public class ScreenLayoutTests
{
    /// <summary>A list column keeps the width it was designed with whenever the screen can afford it.</summary>
    [Test]
    public async Task SplitWidth_KeepsTheDesignedWidthWhenThereIsRoom()
    {
        await Assert.That(ScreenChrome.SplitWidth(120, desired: 56, minimum: 40, companion: 48)).IsEqualTo(56);
        await Assert.That(ScreenChrome.SplitWidth(160, desired: 56, minimum: 40, companion: 48)).IsEqualTo(56);
    }

    /// <summary>
    /// …and gives cells back when there aren't, so the column beside it keeps the width it needs to be
    /// read in. This is the case that was broken: at 100 columns the list took its full 56 and the
    /// editor was left with 42 for content that needs 48.
    /// </summary>
    [Test]
    public async Task SplitWidth_GivesCellsBackRatherThanStarveTheOtherColumn()
    {
        var list = ScreenChrome.SplitWidth(100, desired: 56, minimum: 40, companion: 48);

        await Assert.That(list).IsEqualTo(50);
        await Assert.That(100 - list - ScreenChrome.ColumnDivider).IsGreaterThanOrEqualTo(48);
    }

    /// <summary>
    /// It never goes below the minimum. Past that point both columns are unreadable rather than one,
    /// and a screen that narrow has to clip somewhere whatever it does.
    /// </summary>
    [Test]
    public async Task SplitWidth_StopsAtTheMinimumAndIgnoresAWidthItDoesNotHave()
    {
        await Assert.That(ScreenChrome.SplitWidth(60, desired: 56, minimum: 40, companion: 48)).IsEqualTo(40);

        // No width to reason from — the merged Render the renderer tests go through — is the width the
        // column was always laid out at.
        await Assert.That(ScreenChrome.SplitWidth(0, desired: 56, minimum: 40, companion: 48)).IsEqualTo(56);
    }

    /// <summary>
    /// Compacting spends a block's blank separators, and only those, to make it fit. They carry nothing,
    /// and every row one costs at the top is a row lost off the bottom — where a pane's checkboxes and
    /// buttons live.
    /// </summary>
    [Test]
    public async Task Compact_DropsBlankSeparatorsUntilTheBlockFits()
    {
        var block = new List<string> { "a", string.Empty, "b", string.Empty, "c", string.Empty, "d" };

        await Assert.That(ScreenChrome.Compact(new List<string>(block), 5))
            .IsEquivalentTo(new List<string> { "a", "b", "c", string.Empty, "d" });

        // From the top down, so the section that compacts is the one already on screen.
        await Assert.That(ScreenChrome.Compact(new List<string>(block), 6))
            .IsEquivalentTo(new List<string> { "a", "b", string.Empty, "c", string.Empty, "d" });
    }

    /// <summary>A block that already fits, or one with no height to fit into, is left exactly as it is.</summary>
    [Test]
    public async Task Compact_LeavesABlockThatFitsAlone()
    {
        var block = new List<string> { "a", string.Empty, "b" };

        await Assert.That(ScreenChrome.Compact(new List<string>(block), 3)).IsEquivalentTo(block);
        await Assert.That(ScreenChrome.Compact(new List<string>(block), 0)).IsEquivalentTo(block);
    }

    /// <summary>
    /// A pane taller than its slot scrolls to the row the keyboard is on, so a cursor can never be moved
    /// onto a row that was never drawn. The window is centred, because these blocks are rebuilt from
    /// scratch on every keystroke and there is no previous offset to scroll from.
    /// </summary>
    [Test]
    public async Task Window_KeepsTheCursorRowOnScreenAndSaysWhatItIsHiding()
    {
        var block = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            block.Add(ScreenChrome.Cursor($"row {i}", i == 6, 20));
        }

        var window = ScreenChrome.Window(block, 5);

        await Assert.That(window).Count().IsEqualTo(5);
        await Assert.That(window.Any(l => l.Contains("row 6"))).IsTrue();

        // The edges name the rows they are standing in for: a row silently missing from a pane is the
        // same failure as a cursor stop that was never drawn, one level up.
        await Assert.That(window[0]).Contains("⌃");
        await Assert.That(window[0]).Contains("more");
        await Assert.That(window[^1]).Contains("⌄");
    }

    /// <summary>
    /// With the keyboard in another pane there is no cursor to scroll to, so the block shows its top —
    /// which is where its own heading is.
    /// </summary>
    [Test]
    public async Task Window_ShowsTheTopWhenNothingInTheBlockIsFocused()
    {
        var block = new List<string> { "one", "two", "three", "four" };
        var window = ScreenChrome.Window(block, 2);

        await Assert.That(window[0]).IsEqualTo("one");
        await Assert.That(window[^1]).Contains("⌄ 2 more");
    }

    /// <summary>A block that fits is handed straight back, edges and all.</summary>
    [Test]
    public async Task Window_LeavesABlockThatFitsAlone()
    {
        var block = new List<string> { "one", "two" };

        await Assert.That(ScreenChrome.Window(block, 2)).IsEquivalentTo(block);
        await Assert.That(ScreenChrome.Window(block, 0)).IsEquivalentTo(block);
    }

    /// <summary>
    /// F2's editor pane runs to two dozen rows and its last three are cursor stops. On a 24-row terminal
    /// it used to stop at <c>respond</c>, leaving <c>gag line</c>, <c>stop processing</c> and
    /// <c>case sensitive</c> reachable by ↑↓ and drawn nowhere; compacting the separators brings the
    /// whole pane back inside the body.
    /// </summary>
    [Test]
    public async Task TriggersEditor_FitsAShortScreenWithoutLosingItsCheckboxes()
    {
        var sets = TriggerScene();

        var tall = TriggersScreenRenderer.EditorColumn(sets, 0, Array.Empty<string>());
        var short_ = TriggersScreenRenderer.EditorColumn(
            sets, 0, Array.Empty<string>(), null, TriggersScreenRenderer.ColumnWidth, height: 22);

        await Assert.That(tall.Count).IsGreaterThan(22);
        await Assert.That(short_.Count).IsLessThanOrEqualTo(22);

        foreach (var row in new[] { "gag line", "stop processing", "case sensitive", "attrs", "strikethrough" })
        {
            await Assert.That(short_.Any(l => l.Contains(row))).IsTrue().Because(row + " is still drawn");
        }
    }

    /// <summary>
    /// F5's detail column draws rows belonging to three of the screen's four cursor panes. At 100×24 it
    /// had twelve rows for nineteen and drew the first twelve, which stopped one line above the
    /// CHARACTERS list — so every character row and every button under them was a stop the screen had
    /// never drawn. It compacts, then scrolls to the cursor.
    /// </summary>
    [Test]
    public async Task WorldsDetail_ScrollsToTheCharacterTheCursorIsOn()
    {
        var worlds = WorldScene();
        var focus = new ScreenFocus(WorldsScreenRenderer.CharactersPane, 1, null);

        var column = WorldsScreenRenderer.DetailColumn(
            worlds, Array.Empty<TriggerSet>(), 0, 1, ScreenPalette.Accent, focus, height: 12);

        await Assert.That(column).Count().IsLessThanOrEqualTo(12);
        await Assert.That(column.Any(l => l.Contains("Rookery"))).IsTrue();
        await Assert.That(column.Any(l => l.Contains(ScreenChrome.RemovesWord))).IsTrue();
        await Assert.That(column[0]).Contains("⌃");
    }

    /// <summary>
    /// The WORLDS column compacts and scrolls too, for the reason the detail column does: each world costs
    /// three rows, so at 100×24 two worlds and their buttons ran to twelve rows in ten — and the two rows
    /// that fell off the bottom were <c>[[+ world]]</c> and the row naming what Delete would take. A cursor
    /// stop that was never drawn is the exact failure <c>ScreenChrome.Window</c> exists to prevent, and this
    /// column was the one place on the screen still missing it.
    /// </summary>
    [Test]
    public async Task WorldsList_KeepsItsAddButtonOnScreenWhenTheColumnIsShort()
    {
        var worlds = WorldScene();

        var tall = WorldsScreenRenderer.WorldsColumn(worlds, 0);
        await Assert.That(tall.Any(l => l.Contains(WorldsScreenRenderer.AddWorldLabel))).IsTrue();

        var column = WorldsScreenRenderer.WorldsColumn(worlds, 0, ScreenFocus.None, height: 10);

        await Assert.That(column).Count().IsLessThanOrEqualTo(10);
        await Assert.That(column.Any(l => l.Contains(WorldsScreenRenderer.AddWorldLabel))).IsTrue();
        await Assert.That(column.Any(l => l.Contains(ScreenChrome.RemovesWord))).IsTrue();
    }

    /// <summary>
    /// F4's numpad grid asks for the width its longest bound command needs, instead of ellipsising at a
    /// constant ten characters beside a binding list drawing the same command in full.
    /// </summary>
    [Test]
    public async Task NumpadWidth_FollowsTheLongestCommandBoundToADigit()
    {
        var narrow = new List<Macro> { new() { Key = "Num5", Command = "look" } };
        var wide = new List<Macro> { new() { Key = "Num1", Command = "look at altar" } };

        await Assert.That(KeypadScreenRenderer.NumpadWidth(wide))
            .IsGreaterThan(KeypadScreenRenderer.NumpadWidth(narrow));

        // Given that width, the cell draws the command whole — the asymmetry the review named.
        var grid = KeypadScreenRenderer.NumpadColumn(wide, KeypadScreenRenderer.NumpadWidth(wide));
        await Assert.That(grid.Single(l => l.Contains("[[1]]"))).Contains("look at altar");
    }

    /// <summary>
    /// …and it is bounded. The grid is a diagram of nine keys, not a command line, and every cell it
    /// takes comes out of the binding rows beside it.
    /// </summary>
    [Test]
    public async Task NumpadWidth_IsCappedSoOneLongCommandCannotSwallowTheScreen()
    {
        var absurd = new List<Macro>
        {
            new() { Key = "Num5", Command = new string('x', 200) },
        };

        await Assert.That(KeypadScreenRenderer.NumpadWidth(absurd)).IsLessThan(80);
    }

    /// <summary>
    /// An armed key capture swaps a binding row's key well for a prompt twice its width, so while it is
    /// up the list needs more than its resting minimum — and takes it from the diagram, which has
    /// nothing to do with the keystroke being waited for. Without this the capture row lost its command
    /// off the right-hand edge at 120 columns, which is the width the screen is normally used at.
    /// </summary>
    [Test]
    public async Task ArmedCapture_TakesItsExtraWidthFromTheNumpad()
    {
        var wide = new List<Macro> { new() { Key = "Num1", Command = "look at altar" } };
        var desired = KeypadScreenRenderer.NumpadWidth(wide);

        var resting = ScreenChrome.SplitWidth(
            120, desired, KeypadScreenRenderer.MinNumpadWidth, KeypadScreenRenderer.MinHotkeysWidth);
        var armed = ScreenChrome.SplitWidth(
            120, desired, KeypadScreenRenderer.MinNumpadWidth, KeypadScreenRenderer.CaptureWidth);

        await Assert.That(armed).IsLessThan(resting);
        await Assert.That(120 - armed - ScreenChrome.ColumnDivider)
            .IsGreaterThanOrEqualTo(KeypadScreenRenderer.CaptureWidth);
    }

    private static List<TriggerSet> TriggerScene() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new()
                {
                    Name = "public",
                    Pattern = "^\\[public\\] (.+)$",
                    Enabled = true,
                    Actions = new TriggerActions { SpawnTarget = "Chat", Rewrite = "$1" },
                },
            },
        },
    };

    private static List<WorldDefinition> WorldScene() => new()
    {
        new WorldDefinition
        {
            Name = "Aetherfall",
            Host = "aetherfall.mux",
            Port = 4201,
            Characters = new List<CharacterDefinition>
            {
                new() { Name = "Corvid" },
                new() { Name = "Rookery" },
            },
        },
    };
}
