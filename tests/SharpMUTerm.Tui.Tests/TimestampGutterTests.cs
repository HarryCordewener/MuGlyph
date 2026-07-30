using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The output view's timestamp column, asserted on <b>painted cells</b>.
/// <para>
/// The absence of this file is what let the reported defect ship. <c>CommandDispatchTests</c> already
/// proved every catalog id <em>resolves</em> to a case, and <c>term:timestamps-on</c> resolved to one —
/// so the surface was green while the command did nothing a reader could see. The gutter was baked into
/// a line's markup by <c>AppendWindowLine</c> at the moment the line arrived, which made the setting an
/// append-time decision: flipping it left every line already on screen exactly as it was, and on a quiet
/// connection nothing happened at all. "The show/hide timestamps button seems to not do anything?"
/// </para>
/// <para>
/// Every assertion here therefore reads the frame the driver emitted. Asserting the flag — or the
/// catalog label, or the markup handed to a control — would reproduce the same blind spot: all three
/// were already correct while the screen was wrong.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the reason every frame-rendering suite here is: rendering redirects the process-global
/// <c>Console.Out</c>, and the harness redirects <c>Console.In</c>.
/// </remarks>
[NotInParallel]
public class TimestampGutterTests
{
    private const int Width = 120;
    private const int Height = 32;

    /// <summary>The gutter a headless frame draws — <c>SharpMUTermApp.StampNow</c>'s fixed clock.</summary>
    private const string Gutter = "09:24";

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private static SharpMUTermApp App()
    {
        // The window system reads the console for input even headless; a null reader returns EOF.
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));
    }

    /// <summary>
    /// The frame as rows of text, walked the way a terminal walks it: the cursor-addressing moves the
    /// write position and everything printable lands where it points. The only trustworthy answer to
    /// "is the gutter on that line" is the cells the driver actually emitted.
    /// </summary>
    private static string[] Rows(string ansi)
    {
        var cells = new char[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                cells[y, x] = ' ';
            }
        }

        var (row, column) = (0, 0);
        foreach (Match token in Regex.Matches(ansi, @"\x1b\[([0-9;]*)([A-Za-z])|([^\x1b\r\n])"))
        {
            if (token.Groups[3].Success)
            {
                if (row < Height && column < Width)
                {
                    cells[row, column] = token.Groups[3].Value[0];
                }

                column++;
                continue;
            }

            if (token.Groups[2].Value == "H")
            {
                var at = token.Groups[1].Value.Split(';');
                row = at[0].Length > 0 ? int.Parse(at[0]) - 1 : 0;
                column = at.Length > 1 && at[1].Length > 0 ? int.Parse(at[1]) - 1 : 0;
            }
        }

        var lines = new string[Height];
        for (var y = 0; y < Height; y++)
        {
            var buffer = new char[Width];
            for (var x = 0; x < Width; x++)
            {
                buffer[x] = cells[y, x];
            }

            lines[y] = new string(buffer);
        }

        return lines;
    }

    /// <summary>
    /// The frame a following viewport has settled on. Two renders, because auto-scroll moves the offset
    /// during paint and the frame that discovers new content is one frame stale.
    /// </summary>
    private static string[] SettledRows(SharpMUTermApp app)
    {
        app.RenderWholeFrame();
        return Rows(app.RenderWholeFrame());
    }

    /// <summary>
    /// The row carrying <paramref name="text"/>, or null. Named text rather than a row number because a
    /// pane's rows move as the rail's width and the wrapping change, and the claim is about a line.
    /// </summary>
    private static string? RowWith(string[] rows, string text) =>
        rows.FirstOrDefault(r => r.Contains(text, StringComparison.Ordinal));

    /// <summary>Whether that line is drawn with the gutter ahead of its text — the visible setting.</summary>
    private static bool Stamped(string[] rows, string text) =>
        RowWith(rows, text) is { } row &&
        row.IndexOf(Gutter, StringComparison.Ordinal) >= 0 &&
        row.IndexOf(Gutter, StringComparison.Ordinal) < row.IndexOf(text, StringComparison.Ordinal);

    // --- the bug ---------------------------------------------------------------------------------

    /// <summary>
    /// <b>The one that would have caught it.</b> With output already on screen and the column off,
    /// <c>term:timestamps-on</c> puts the gutter on the lines that were <em>already there</em>.
    /// <para>
    /// Under the old code this fails on the last assertion: the first frame is right, the command runs,
    /// and the second frame is byte-identical to the first, because the gutter every one of those lines
    /// would ever have was decided when it was appended.
    /// </para>
    /// </summary>
    [Test]
    public async Task TurningTheColumnOnStampsTheOutputThatIsAlreadyOnScreen()
    {
        var app = App();

        // The demo scene, rendered with the column off: real output, no gutter anywhere.
        var before = Rows(app.RenderSnapshot());
        await Assert.That(RowWith(before, "The Grand Plaza")).IsNotNull();
        await Assert.That(before.Any(r => r.Contains(Gutter, StringComparison.Ordinal))).IsFalse();

        app.DispatchCommand("term:timestamps-on");
        var after = SettledRows(app);

        // The same lines, now with the column — history, not just whatever arrives next.
        await Assert.That(Stamped(after, "The Grand Plaza")).IsTrue();
        await Assert.That(Stamped(after, "A town guard stands watch")).IsTrue();
    }

    /// <summary>
    /// And it reaches a <b>spawn</b> window, which is the whole reason the gutter had to leave the line's
    /// markup rather than be re-rendered from a session's scrollback. A session window's history exists
    /// as <c>StyledLine</c>s in <c>WorldSession.Scrollback</c> and could have been rebuilt; a spawn
    /// window's exists only as markup in the app's own line buffer, and there is nothing left to
    /// re-render. A fix that restamped one and not the other would make the same command visibly do
    /// different things in different tabs.
    /// </summary>
    [Test]
    public async Task ASpawnWindowsHistoryTakesTheColumnToo()
    {
        var app = App();

        // The `spawn` view brings the trigger-routed Chat window forward, so its backlog is on screen.
        var before = Rows(app.RenderSnapshot("spawn"));
        await Assert.That(RowWith(before, "Rivane: anyone up for the crypt run?")).IsNotNull();
        await Assert.That(before.Any(r => r.Contains(Gutter, StringComparison.Ordinal))).IsFalse();

        app.DispatchCommand("term:timestamps-on");
        var after = SettledRows(app);

        await Assert.That(Stamped(after, "Rivane: anyone up for the crypt run?")).IsTrue();
        await Assert.That(Stamped(after, "Bob: aye, meet me at the gate")).IsTrue();
    }

    /// <summary>Turning it off again takes the column back off the history it put it on.</summary>
    [Test]
    public async Task TurningTheColumnOffTakesItBackOffWhatIsOnScreen()
    {
        var app = App();
        app.RenderSnapshot();

        app.DispatchCommand("term:timestamps-on");
        await Assert.That(Stamped(SettledRows(app), "The Grand Plaza")).IsTrue();

        app.DispatchCommand("term:timestamps-off");
        var after = SettledRows(app);

        await Assert.That(RowWith(after, "The Grand Plaza")).IsNotNull();
        await Assert.That(after.Any(r => r.Contains(Gutter, StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// A frozen pane takes it on <b>both</b> halves, and stays a frozen pane. Freezing splits a window's
    /// buffer at the length it had when ⌃F was pressed and draws the two sides into two controls, so a
    /// repaint that fed one control the whole buffer would slide the pinned scrollback into the live
    /// tail and duplicate every line above the bar. The <c>freeze</c> view puts the demo scene above the
    /// split and two courier lines below it — one assertion for each side.
    /// </summary>
    [Test]
    public async Task AFrozenPanesPinnedHistoryAndItsLiveTailBothTakeTheColumn()
    {
        var app = App();

        var before = Rows(app.RenderSnapshot("freeze"));
        await Assert.That(RowWith(before, "FROZEN")).IsNotNull();
        await Assert.That(before.Any(r => r.Contains(Gutter, StringComparison.Ordinal))).IsFalse();

        app.DispatchCommand("term:timestamps-on");
        var after = SettledRows(app);

        await Assert.That(Stamped(after, "The Grand Plaza")).IsTrue();              // pinned half
        await Assert.That(Stamped(after, "A courier jogs in from the east")).IsTrue(); // live tail
        await Assert.That(RowWith(after, "FROZEN")).IsNotNull();

        // And nothing was duplicated across the bar: the pinned half's first line appears once.
        await Assert.That(after.Count(r => r.Contains("The Grand Plaza", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    // --- the ids mean what they say ---------------------------------------------------------------

    /// <summary>
    /// <c>term:timestamps-on</c> turns them <b>on</b> — including when they already are. Both ids used to
    /// run one <c>!</c> flip, so the entry labelled <c>Show timestamps</c> was a toggle wearing a
    /// statement's name: any desync between the catalog's idea of the state and the app's made it do the
    /// opposite of what it said, and running it twice made it do the opposite of what it just did.
    /// </summary>
    [Test]
    public async Task TimestampsOnTwiceLeavesThemOn()
    {
        var app = App();
        app.RenderSnapshot();

        app.DispatchCommand("term:timestamps-on");
        app.DispatchCommand("term:timestamps-on");

        await Assert.That(Stamped(SettledRows(app), "The Grand Plaza")).IsTrue();
    }

    /// <summary>And <c>term:timestamps-off</c> turns them off, including when they already are.</summary>
    [Test]
    public async Task TimestampsOffTwiceLeavesThemOff()
    {
        var app = App();
        app.RenderSnapshot();

        app.DispatchCommand("term:timestamps-off");
        app.DispatchCommand("term:timestamps-off");
        var after = SettledRows(app);

        await Assert.That(RowWith(after, "The Grand Plaza")).IsNotNull();
        await Assert.That(after.Any(r => r.Contains(Gutter, StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// The catalog offers the id that changes the state it is actually in. This is the half of the desync
    /// the app owns: the label and the behaviour are now read from one field, so an entry reading
    /// <c>Hide timestamps</c> cannot be an entry that shows them.
    /// </summary>
    [Test]
    public async Task TheCatalogOffersTheEntryThatUndoesTheStateOnScreen()
    {
        var app = App();
        app.RenderSnapshot();

        await Assert.That(app.BuildCatalog().Any(i => i.Id == "term:timestamps-on")).IsTrue();

        app.DispatchCommand("term:timestamps-on");

        await Assert.That(app.BuildCatalog().Any(i => i.Id == "term:timestamps-off")).IsTrue();
        await Assert.That(app.BuildCatalog().Any(i => i.Id == "term:timestamps-on")).IsFalse();
    }

    // --- the setting is a preference, and is kept ------------------------------------------------

    /// <summary>
    /// The column is a preference about how this terminal draws text, not a property of a pane that only
    /// exists for this run, so it is written to the configuration when it changes — and read back from
    /// it, which is what makes a new app open with the column the last one was left with.
    /// </summary>
    [Test]
    public async Task TheColumnIsWrittenToTheConfigurationAndReadBackFromIt()
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        var saved = 0;
        var app = new SharpMUTermApp(
            config, Headless, new HeadlessConsoleDriver(Width, Height), save: _ => saved++);

        app.RenderSnapshot();
        app.DispatchCommand("term:timestamps-on");

        await Assert.That(config.Text.ShowTimestamps).IsTrue();
        await Assert.That(saved).IsGreaterThan(0);

        // A fresh app over the same configuration opens with the column already on — the setting is
        // read, not merely stored.
        Console.SetIn(TextReader.Null);
        var reopened = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        await Assert.That(Stamped(Rows(reopened.RenderSnapshot()), "The Grand Plaza")).IsTrue();
    }

    /// <summary>
    /// It does <em>not</em> persist through <c>SaveConfiguration</c>, which is the settings screens'
    /// funnel and does more than write: it re-hands every live session its trigger sets, and
    /// re-periodising a running timer resets every other timer's phase. A view preference must not have
    /// that side effect, so the toggle goes through the narrow write instead — and an app with no save
    /// action still writes nothing at all, exactly as a snapshot must.
    /// </summary>
    [Test]
    public async Task AnAppWithNoSaveActionStillWritesNothingWhenTheColumnIsToggled()
    {
        var config = DemoScene.Build();
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));

        app.RenderSnapshot();
        app.DispatchCommand("term:timestamps-on");

        // Nothing threw, the live state changed, and the only route to disk is the action this app was
        // never handed.
        await Assert.That(Stamped(SettledRows(app), "The Grand Plaza")).IsTrue();
        await Assert.That(config.Text.ShowTimestamps).IsTrue();
    }

    // --- the two frames the snapshot pipeline pins ------------------------------------------------

    /// <summary>
    /// The two <c>timestamps</c> views agree cell for cell. One sets the column before the scene loads
    /// and the other dispatches the real ⌃P entry after it is on screen — and a render-time gutter is
    /// exactly the claim that those two are the same picture. Under the old code the second was
    /// identical to the default workspace instead.
    /// </summary>
    [Test]
    public async Task TheColumnLooksTheSameWhetherItWasOnAllAlongOrTurnedOnAfterwards()
    {
        var seeded = Rows(App().RenderSnapshot("timestamps"));
        var toggled = Rows(App().RenderSnapshot("timestamps-toggled"));

        // `timestamps-toggled` splits the workspace, so compare the one pane both frames draw the same
        // way: the main window's own lines, gutter included.
        await Assert.That(Stamped(seeded, "The Grand Plaza")).IsTrue();
        await Assert.That(Stamped(toggled, "The Grand Plaza")).IsTrue();
        // The spawn window is in the narrower right-hand pane there, so its line wraps — match the part
        // of it that stays on the stamped row.
        await Assert.That(Stamped(toggled, "Rivane: anyone up for the crypt")).IsTrue();
    }
}
