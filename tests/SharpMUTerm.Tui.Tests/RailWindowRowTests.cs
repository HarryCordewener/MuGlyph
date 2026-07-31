using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The connection rail's window rows: what they are called, and that they fit the column they are drawn in.
/// <para>
/// Two defects, reported as one. The row under <c>Convergence MUSH ▸ Mannaz</c> read
/// <c>Convergence MUSH   main</c> — a window titled for its <em>world</em>, drawn under the character, which
/// is drawn under that same world, so the one thing the row spelt out was the one thing both its ancestors
/// had already said. And it <em>wrapped</em>, which is what made it look broken rather than merely
/// redundant: the sidebar's width is derived from its widest row but was only ever recomputed when the pane
/// area was rebuilt, so the startup retitle (<c>Main</c> → the world's name) poured a long row into a column
/// sized for a short one.
/// </para>
/// <para>
/// No snapshot caught either, because <see cref="DemoScene"/> titled its main window <c>main</c> while a
/// live session titles it <c>session.World.Name</c> — the frames were checking a shape the running client
/// never has. <see cref="TheDemoScenesMainWindowIsTitledTheWayALiveSessionTitlesIt"/> holds the two together.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class RailWindowRowTests
{
    private const int Width = 120;
    private const int Height = 34;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    // ---- the label ---------------------------------------------------------------------------

    /// <summary>
    /// A character's own session window reads <c>main</c>, and the world's name appears once — on the world
    /// row that establishes it. Asserted on the rail the app actually draws, under a world whose name is
    /// long enough that a repeat is unmistakable.
    /// </summary>
    [Test]
    public async Task ACharactersOwnWindowRowReadsMainRatherThanRepeatingTheWorld()
    {
        var app = await LongWorld();

        var rows = Rail(app);
        var window = rows.Single(r => r.Contains('▪', StringComparison.Ordinal));

        await Assert.That(window.Trim()).IsEqualTo("▪ main");
        await Assert.That(rows.Count(r => r.Contains("Convergence MUSH", StringComparison.Ordinal))).IsEqualTo(1);
    }

    /// <summary>
    /// A spawn window keeps its own name: the label says <em>which</em> of a character's windows a row is,
    /// and "main" is only the answer for the one that is the session's own.
    /// </summary>
    [Test]
    public async Task ASpawnWindowRowKeepsItsTargetName()
    {
        var app = App();
        app.RenderSnapshot(); // the demo resumes Corvid's main window plus a Chat spawn

        var windows = Rail(app).Where(r => r.Contains('▪', StringComparison.Ordinal)).ToList();

        // The demo leaves a line half-typed in the main window, so that row also carries the ✎ pen. The
        // gaps are the reserved badge fields: the pen's two cells and the unread count's three are always
        // there, blank when there is nothing to put in them, so that a keystroke or a line of output
        // cannot resize the sidebar (see RailRenderer.UnsentFieldWidth).
        // The chord *leads* each row: the demo holds two of Corvid's windows, so ⌥1 and ⌥2 both name
        // somewhere to go, and each sits against the name it belongs to rather than behind two blank
        // badge fields (see RailChordColumnTests, which is where that reordering is pinned).
        await Assert.That(windows.Select(r => r.TrimEnd()).Select(r => r.TrimStart()).ToList())
            .IsEquivalentTo(new[] { "⌥1 ▪ main " + Glyphs.Draft, "⌥2 ▪ Chat    2" });
    }

    /// <summary>
    /// <b>The two columns never wear the same word.</b> The second column used to be the hosting pane and
    /// it called the first pane "main" too, so a row could read <c>▪ main   main</c> — the naive fix for
    /// the label, and two different meanings in one line. It is now the <c>⌥N</c> that goes to the window
    /// the row <em>is</em>, which no window title can be mistaken for, and which the panes' own
    /// <c>pane N</c> vocabulary cannot be mistaken for either.
    /// </summary>
    [Test]
    public async Task TheChordColumnNeverRepeatsTheWindowsOwnName()
    {
        var app = App();
        app.RenderSnapshot("split"); // two panes, so both windows are visible at once
        app.RenderNextFrame();

        var windows = Rail(app).Where(r => r.Contains('▪', StringComparison.Ordinal)).ToList();
        await Assert.That(windows).IsNotEmpty();

        foreach (var row in windows)
        {
            await Assert.That(row).Contains("⌥");
            await Assert.That(row.Trim()).IsNotEqualTo("▪ main   main");
            await Assert.That(row)
                .DoesNotContain("pane ")
                .Because("panes are numbered separately, and a pane noun here would be a second reading of ⌥N");
        }
    }

    /// <summary>
    /// <b>With one window the column is not drawn at all</b> — there is one place to be, so naming it says
    /// nothing, and the three cells it costs come out of the pane area through the rail's width.
    /// <para>
    /// The condition is the <em>window</em> count and no longer the pane count: the column named the
    /// hosting pane until ⌥N was given to windows, so it appeared only in a split. It now appears as soon
    /// as there is somewhere else to go, which on a fresh client with one capture open is immediately —
    /// and that is the point, because the capture is what the chord was asked to reach.
    /// </para>
    /// </summary>
    [Test]
    public async Task WithOneWindowTheChordColumnIsNotDrawn()
    {
        var app = OneWindow();
        app.RenderSnapshot();

        await Assert.That(app.NumberedWindowIds.Count).IsEqualTo(1);
        foreach (var row in Rail(app).Where(r => r.Contains('▪', StringComparison.Ordinal)))
        {
            await Assert.That(row).DoesNotContain("⌥");
        }

        // The rows do end in blanks, and that is the reserved badge fields rather than slack — so the
        // claim this used to make with DoesNotEndWith(" ") is made by width instead, which is the thing
        // that actually mattered: a rail with nothing to say in this column must not pay for it.
        var single = MainWindowRowWidth(app);

        var two = App();
        two.RenderSnapshot(); // the demo resumes with a Chat capture beside the main window
        await Assert.That(two.NumberedWindowIds.Count).IsEqualTo(2);

        await Assert.That(Rail(two).Single(MainRow)).Contains("⌥");
        await Assert.That(MainWindowRowWidth(two))
            .IsEqualTo(single + 3)
            .Because("the column is `⌥N` and one space — three cells, and only when it has something to say");
    }

    /// <summary>The demo scene with its capture window taken out, so one window is left in one pane.</summary>
    private static SharpMUTermApp OneWindow()
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        config.LastSession!.Windows.RemoveAll(w => w.Kind == WindowKind.Spawn);
        config.LastSession.Root.Tabs.RemoveAll(t => t.StartsWith("spawn:", StringComparison.Ordinal));
        return new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
    }

    private static bool MainRow(string row) =>
        row.Contains("▪ main", StringComparison.Ordinal);

    private static int MainWindowRowWidth(SharpMUTermApp app) =>
        app.RailLines.Select(SharpMUTermApp.MarkupWidth)
            .Zip(Rail(app), (width, plain) => (width, plain))
            .Single(r => MainRow(r.plain)).width;

    /// <summary>
    /// Closing a window takes its row away rather than marking it <c>closed</c>, because
    /// <c>SharpMUTermApp.CloseWindow</c> forgets the window entirely (<c>Workspace.CloseWindow</c> removes
    /// it from the registry as well as from its pane). The renderer's <c>closed</c> arm is therefore
    /// unreachable from the shell today — it is pinned at the renderer level by <c>RailRendererTests</c> —
    /// and this says so, so the next reader does not go looking for the row that is missing.
    /// </summary>
    [Test]
    public async Task ClosingAWindowRemovesItsRowRatherThanMarkingItClosed()
    {
        var app = App();
        app.RenderSnapshot();
        var chat = Workspace.SpawnWindowId("Chat");
        await Assert.That(string.Join("\n", Rail(app))).Contains("Chat");

        await Assert.That(app.DispatchCommand("win:" + chat)).IsTrue(); // bring it up so ⌃W can close it
        await Assert.That(app.DispatchCommand("layout:close")).IsTrue();
        app.RenderNextFrame();

        await Assert.That(string.Join("\n", Rail(app))).DoesNotContain("Chat");
        await Assert.That(string.Join("\n", Rail(app))).DoesNotContain("closed");
    }

    // ---- the wrap ----------------------------------------------------------------------------

    /// <summary>
    /// <b>No rail row is wider than the rail.</b> The reproduction of the report: a world whose name is long
    /// enough that the row needs more columns than the sidebar was built with, retitled the way the startup
    /// path retitles it — after the pane area was built. The invariant is the one a wrapped row breaks, and
    /// it is asserted with the same measure the layout sizes the column by.
    /// </summary>
    [Test]
    public async Task NoRailRowIsWiderThanTheRailColumn()
    {
        var app = await LongWorld();

        var widest = Rail(app).Max(SharpMUTermApp.MarkupWidth);
        await Assert.That(widest).IsGreaterThan(0);
        await Assert.That(app.RailColumnWidth).IsGreaterThanOrEqualTo(widest);
    }

    /// <summary>
    /// The same invariant after the rows grow again for a different reason — a page loaded into the web
    /// view, whose title becomes a rail row. <c>ShowWeb</c> rebuilds the pane area only for a
    /// <em>first</em> page, so the second one is the case that used to wrap.
    /// </summary>
    [Test]
    public async Task LoadingASecondWebPageDoesNotOutgrowTheRail()
    {
        var app = App();
        app.RenderSnapshot("web");
        app.SimulateWebPage(); // the window already exists: retitle, no rebuild
        app.RenderNextFrame();

        var widest = Rail(app).Max(SharpMUTermApp.MarkupWidth);
        await Assert.That(app.RailColumnWidth).IsGreaterThanOrEqualTo(widest);
    }

    /// <summary>
    /// A label longer than the sidebar's own cap is <b>elided</b>, not wrapped. The rail's width is the
    /// widest row's <em>clamped</em>, so any name past the clamp used to run onto a second line — and a web
    /// page title is as long as a world cares to make it, which is the reachable case. The row is one line
    /// wide, no wider than the column, and it says which window it is up to the ellipsis.
    /// </summary>
    [Test]
    public async Task ALabelLongerThanTheRailIsElidedRatherThanWrapped()
    {
        var app = App();
        app.RenderSnapshot();
        app.SimulateWebPageTitled(new string('W', 200));
        app.RenderNextFrame();

        var rows = Rail(app);
        var web = rows.Single(r => r.Contains("WWW", StringComparison.Ordinal));

        // Not EndsWith: the row's reserved badge fields sit after the label, so the ellipsis is followed
        // by blanks. What is being claimed is that the label gave ground rather than the row wrapping,
        // and the width cap below is the half of it the layout depends on.
        await Assert.That(web).Contains("…");
        foreach (var row in app.RailLines)
        {
            await Assert.That(SharpMUTermApp.MarkupWidth(row)).IsLessThanOrEqualTo(app.RailColumnWidth);
        }
    }

    /// <summary>
    /// Shortening the rows narrows the sidebar again, rather than leaving it as wide as the widest thing it
    /// ever held: the columns the rail gives back are columns the panes get, and per-pane NAWS is derived
    /// from the pane rectangle.
    /// </summary>
    [Test]
    public async Task ClosingTheWidestRowGivesTheColumnsBackToThePanes()
    {
        var app = App();
        app.RenderSnapshot("web");
        var wide = app.RailColumnWidth;
        var paneWide = app.PaneOutputRects()[app.FocusedPaneId].Width;

        await Assert.That(app.DispatchCommand("win:web")).IsTrue();
        await Assert.That(app.DispatchCommand("layout:close")).IsTrue();
        app.RenderNextFrame();

        await Assert.That(app.RailColumnWidth).IsLessThan(wide);
        await Assert.That(app.PaneOutputRects()[app.FocusedPaneId].Width).IsGreaterThan(paneWide);
    }

    // ---- the demo and the live shape ---------------------------------------------------------

    /// <summary>
    /// The gap that hid all of this: the demo's saved main-window title has to be what
    /// <c>BindSession</c> writes for a live one, or every snapshot renders a shape the client never shows.
    /// Both sides are read here — the demo's saved state, and the title a real session actually leaves on
    /// the window — so neither can drift.
    /// </summary>
    [Test]
    public async Task TheDemoScenesMainWindowIsTitledTheWayALiveSessionTitlesIt()
    {
        var demo = DemoScene.Build();
        var saved = demo.LastSession!.Windows.Single(w => w.Id == "main");

        Console.SetIn(TextReader.Null);
        var live = new SharpMUTermApp(
            new AppConfiguration { Worlds = { demo.Worlds[0] } },
            Headless,
            new HeadlessConsoleDriver(Width, Height));
        var telnet = new RecordingTelnetSession();
        live.TelnetFactory = _ => telnet;
        live.BindWorldWithoutConnecting(demo.Worlds[0]);

        await Assert.That(saved.Title).IsEqualTo(live.WindowTitleOf("main"));
        await Assert.That(saved.Title).IsEqualTo(demo.Worlds[0].Characters[0].Name);
    }

    /// <summary>
    /// And the startup path records who owns the main window. It is built before any session exists, so the
    /// first session adopts one owned by nobody; <c>OpenSessionWindow</c> learned to fix that for the
    /// character-switch path and this is the other door onto the same window. Ownership is what the rail
    /// lists a character's windows by, and what <c>WindowSession</c> resolves a window's typing through.
    /// </summary>
    [Test]
    public async Task TheFirstSessionOwnsTheMainWindowItAdopts()
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration();
        config.Worlds.Add(new WorldDefinition { Name = "Solo", Host = "solo.example.org", Port = 4000 });

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        app.TelnetFactory = _ => new RecordingTelnetSession();
        var session = app.BindWorldWithoutConnecting(config.Worlds[0]);

        await Assert.That(app.WindowOwnerOf("main")).IsEqualTo(session.SessionKey);
    }

    // ---- harness -----------------------------------------------------------------------------

    private static SharpMUTermApp App()
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));
    }

    /// <summary>
    /// The reported world: a name long enough that the rail row needs more columns than the sidebar is built
    /// with, bound through the app's real startup path so the retitle happens where it happens live —
    /// after the pane area exists.
    /// </summary>
    private static async Task<SharpMUTermApp> LongWorld()
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration();
        var world = new WorldDefinition { Name = "Convergence MUSH", Host = "convergence.example.org", Port = 4201 };
        world.Characters.Add(new CharacterDefinition { Name = "Mannaz", Logging = new LoggingSettings() });
        config.Worlds.Add(world);

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        var telnet = new RecordingTelnetSession();
        app.TelnetFactory = _ => telnet;
        var session = app.BindWorldWithoutConnecting(world);
        await session.ConnectAsync();
        app.RenderNextFrame();
        return app;
    }

    /// <summary>
    /// The rail's rows as the app draws them, as visible text: the markup tags — colours, and the
    /// <c>[link=…]</c> spans that make a row clickable — emit no cell, and a row's <em>label</em> is what is
    /// being asserted here. The width assertions use <see cref="SharpMUTermApp.MarkupWidth"/> on the raw
    /// markup instead, because that is the measure the layout sizes the column by.
    /// </summary>
    private static IReadOnlyList<string> Rail(SharpMUTermApp app) =>
        app.RailLines.Select(Plain).ToArray();

    /// <summary>Strips markup tags the way <see cref="SharpMUTermApp.MarkupWidth"/> counts them.</summary>
    private static string Plain(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\[(?!\[)[^\]]*\]", string.Empty)
            .Replace("[[", "[", StringComparison.Ordinal)
            .Replace("]]", "]", StringComparison.Ordinal);
}
