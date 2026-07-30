using SharpConsoleUI.Drivers;
using SharpConsoleUI.Parsing;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The connection rail as a control you can click: a mouse frame into the painted rail, out the other
/// side as a switched character or an activated window. The link hit-test runs against the geometry a
/// real rendered frame recorded, and the payload is read off the rail the app drew — nothing about it
/// is written down here.
/// <para>
/// The rail advertised this before it did it. <c>RailRenderer</c> documented the collapsed strip with
/// "clicking still switches character" while emitting no <c>[link=…]</c> span anywhere and
/// <c>_rail.LinkClicked</c> was never subscribed — a comment describing a feature that did not exist,
/// which is how the next person builds on sand.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason the other end-to-end suites are: constructing the app and rendering
/// a frame both touch the process-global console streams.
/// </remarks>
[NotInParallel]
public class RailClickTests
{
    private const int Width = 120;
    private const int Height = 32;
    private const string Rookery = "Aetherfall.Rookery";
    private const string Thistle = "Grapevine.Thistle";

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private static SharpMUTermApp App(AppConfiguration? config = null)
    {
        // The window system reads the console for input even headless; a null reader returns EOF.
        Console.SetIn(TextReader.Null);
        config ??= DemoScene.Build();
        foreach (var character in config.Worlds.SelectMany(w => w.Characters))
        {
            character.Logging = new LoggingSettings(); // never write a transcript from a test
        }

        return new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
    }

    // ---- A click, for real -----------------------------------------------------------------

    /// <summary>
    /// The feature, end to end and through the mouse: a click on an offline character's row in the
    /// rendered rail makes it the active session. Nothing about the payload is written down here — it
    /// is read off the frame the app drew, at the cell the name occupies.
    /// </summary>
    [Test]
    public async Task ClickingACharacterRow_SwitchesToIt()
    {
        var app = App();
        app.RenderSnapshot(); // arranging + painting is what gives the links their hit-test geometry

        await Assert.That(app.ActiveSessionKey).IsNull(); // the demo scene draws a rail with no live session

        ClickRailRow(app, "Rookery");

        await Assert.That(app.ActiveSessionKey).IsEqualTo(Rookery);
        await Assert.That(app.FindSession(Rookery)).IsNotNull();
        await Assert.That(app.StatusMarkup).Contains("switched to Rookery");
    }

    /// <summary>A character under another world is reachable the same way — the rail spans all of them.</summary>
    [Test]
    public async Task ClickingACharacterInAnotherWorld_SwitchesToIt()
    {
        var app = App();
        app.RenderSnapshot();

        ClickRailRow(app, "Thistle");

        await Assert.That(app.ActiveSessionKey).IsEqualTo(Thistle);
    }

    /// <summary>
    /// A world row is not itself connectable, so it goes to the character you are in — or, when you are
    /// not in one of its characters, to one of them. Clicking Grapevine while Corvid is active lands on
    /// Thistle, which is the only thing "go to this world" can honestly mean.
    /// </summary>
    [Test]
    public async Task ClickingAWorldRow_SwitchesToOneOfItsCharacters()
    {
        var app = App();
        app.RenderSnapshot();

        ClickRailRow(app, "Grapevine");

        await Assert.That(app.ActiveSessionKey).IsEqualTo(Thistle);
    }

    /// <summary>A window row activates that window in its pane.</summary>
    [Test]
    public async Task ClickingAWindowRow_ActivatesThatWindow()
    {
        var app = App();
        app.RenderSnapshot();

        // The demo's active character owns a spawn window as well as the main one.
        var spawn = app.WindowIds().First(id => id.StartsWith("spawn:", StringComparison.Ordinal));
        var title = SpawnTitle(spawn);
        ClickRailRow(app, title);

        await Assert.That(app.ActiveWindowId()).IsEqualTo(spawn);
    }

    /// <summary>
    /// The collapsed rail (⌃B b) is a strip of initials, and an initial is the only handle it offers —
    /// so clicking one switches character. This is the row the false comment was written about.
    /// </summary>
    [Test]
    public async Task ClickingACollapsedInitial_SwitchesToThatCharacter()
    {
        var app = App();
        app.RenderSnapshot("collapsed");

        var lines = app.RailLines;
        var row = lines.ToList().FindIndex(l => l.Contains("char:" + Rookery, StringComparison.Ordinal));
        await Assert.That(row).IsGreaterThanOrEqualTo(0);
        await Assert.That(SharpMUTermApp.MarkupWidth(lines[row])).IsLessThanOrEqualTo(4); // it really is the strip

        await Assert.That(app.SimulateRailClick(SharpMUTermApp.MarkupWidth(lines[row]) / 2, row)).IsTrue();
        await Assert.That(app.ActiveSessionKey).IsEqualTo(Rookery);
    }

    /// <summary>
    /// Clicking the rail must not leave the caret stranded. A click on a <c>MarkupControl</c> that has
    /// links focuses it (the framework does that before the link is even raised), and in this window
    /// focus belongs to the armed command line — paste and the terminal cursor both follow it.
    /// </summary>
    [Test]
    public async Task ClickingTheRail_LeavesFocusOnTheArmedBar()
    {
        var app = App();
        app.RenderSnapshot();

        ClickRailRow(app, "Rookery");

        await Assert.That(app.ArmedBarHasFocus).IsTrue();
        await Assert.That(app.FocusIsOnAVisibleControl).IsTrue();

        app.SimulatePaste("pose waves.");
        await Assert.That(app.ArmedInputText).EndsWith("pose waves."); // the demo pre-fills the bar
    }

    /// <summary>
    /// The CONNECTIONS header is chrome. A click on it hits no link, so nothing switches — and the
    /// blank column between the widest row and the splitter is inert for the same reason: the link
    /// spans cover row content only, never the tail out to the sidebar's edge.
    /// </summary>
    [Test]
    public async Task ClickingTheHeaderOrTheEmptyColumn_DoesNothing()
    {
        var app = App();
        app.RenderSnapshot();
        var before = app.ActiveSessionKey;

        await Assert.That(app.SimulateRailClick(4, RowIndex(app, "CONNECTIONS"))).IsFalse();
        await Assert.That(app.ActiveSessionKey).IsEqualTo(before);

        // The row Rookery sits on, but out past its text, up against the divider column.
        var edge = RailColumns(app) - 1;
        await Assert.That(app.SimulateRailClick(edge, RowIndex(app, "Rookery"))).IsFalse();
        await Assert.That(app.ActiveSessionKey).IsEqualTo(before);
    }

    // ---- Every target the rail can draw is handled -----------------------------------------

    /// <summary>
    /// The rail's answer to <c>EveryCatalogIdIsHandled</c>. Every link the drawn rail carries is
    /// dispatched for real, and one the handler does not understand fails — the rail is generated from
    /// live state while the handler is a hand-written prefix test, which is exactly the pair that drifts
    /// in silence.
    /// </summary>
    [Test]
    public async Task EveryTargetTheRailDraws_IsHandled()
    {
        var app = App(WithAnEmptyWorld());
        app.RenderSnapshot();

        var targets = Targets(app.RailLines);
        await Assert.That(targets).IsNotEmpty();

        foreach (var target in targets)
        {
            await Assert.That(app.DispatchRailTarget(target))
                .IsTrue()
                .Because($"the rail draws a link to '{target}', so a click on it must do something");
        }
    }

    /// <summary>
    /// A world with no characters already prints "no characters"; clicking it has to say so rather than
    /// look broken. It reports through the transient status mechanism, so it is visible with nothing
    /// connected, clears itself, and stays findable in the ⌃P message log.
    /// </summary>
    [Test]
    public async Task ClickingAWorldWithNoCharacters_SaysSo()
    {
        var app = App(WithAnEmptyWorld());
        app.RenderSnapshot();
        var before = app.ActiveSessionKey;

        ClickRailRow(app, "no characters");

        await Assert.That(app.ActiveSessionKey).IsEqualTo(before); // nothing to switch to, nothing switched
        await Assert.That(app.StatusMarkup).Contains("Hollow has no characters yet");
        await Assert.That(app.Messages.Entries.Any(m => m.Text.Contains("Hollow has no characters"))).IsTrue();
    }

    /// <summary>
    /// A window the layout no longer holds is drawn "closed", and clicking it says it is not open any
    /// more — the same answer the ⌃P entry for that window gives, because the rail hands over the same
    /// id. The row promises a destination that has gone; the click says so instead of appearing dead.
    /// </summary>
    [Test]
    public async Task ClickingAClosedWindow_SaysItIsNotOpen()
    {
        var app = App();
        app.RenderSnapshot();

        var handled = app.DispatchRailTarget("win:spawn:long gone");

        await Assert.That(handled).IsTrue();
        await Assert.That(app.StatusMarkup).Contains("is not open any more");
    }

    /// <summary>An unknown target is refused out loud rather than swallowed.</summary>
    [Test]
    public async Task AnUnknownRailTarget_IsRefused()
    {
        var app = App();

        await Assert.That(app.DispatchRailTarget("rail:nonesuch")).IsFalse();
        await Assert.That(app.StatusMarkup).Contains("nothing here handles it");
    }

    // ---- The trust boundary ----------------------------------------------------------------

    /// <summary>
    /// The rail's targets are not reachable from the handler that serves the output panes. Those panes
    /// render MXP/Pueblo links a <em>world</em> sends over the wire, so anything they can dispatch is
    /// something a hostile or careless server can make this client do. The boundary is the control —
    /// <c>_rail</c> has its own <c>LinkClicked</c> — rather than a URL-scheme convention, and this pins
    /// it rather than trusting the comment that says so.
    /// </summary>
    [Test]
    public async Task RailTargetsAreNotReachableFromThePaneLinkHandler()
    {
        var app = App(WithAnEmptyWorld());
        app.RenderSnapshot();

        var before = app.ActiveSessionKey;

        // Every target the rail can draw, handed to the pane handler exactly as a server-supplied
        // [link=…] would deliver it, plus the two written out by hand in case the drawn rail is ever
        // missing one of the kinds.
        var targets = Targets(app.RailLines).Concat(new[] { "char:" + Thistle, "win:main" }).ToList();
        await Assert.That(targets).Contains("char:" + Rookery);

        foreach (var target in targets)
        {
            app.OnLinkClicked(target);
        }

        // Nothing switched, nothing was connected, and no character's window was opened. (An unknown
        // scheme falls through to the web view, which is what this handler has always done with a link
        // it does not recognise — a picture of an error page, not a hand on the client's controls.)
        await Assert.That(app.ActiveSessionKey).IsEqualTo(before);
        await Assert.That(app.FindSession(Rookery)).IsNull();
        await Assert.That(app.FindSession(Thistle)).IsNull();
        await Assert.That(app.WindowIds().Any(id => id.StartsWith("char:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(app.StatusMarkup).DoesNotContain("switched to");
    }

    /// <summary>
    /// The same boundary, drawn while fixing this one: the header's ☰ menu had its own scheme but shared
    /// the panes' handler, so any world could open this client's command surface with
    /// <c>&lt;a href="sharpmuterm-menu:toggle"&gt;</c>. The chrome now has a handler of its own.
    /// </summary>
    [Test]
    public async Task TheMenuIsNotReachableFromThePaneLinkHandler()
    {
        var app = App();
        app.RenderSnapshot();

        app.OnLinkClicked("sharpmuterm-menu:toggle");

        await Assert.That(app.MenuIsOpen).IsFalse();
    }

    // ---- Helpers ---------------------------------------------------------------------------

    /// <summary>A demo config plus a world with no characters, which the shipped demo has none of.</summary>
    private static AppConfiguration WithAnEmptyWorld()
    {
        var config = DemoScene.Build();
        config.Worlds.Add(new WorldDefinition
        {
            Name = "Hollow",
            Host = "hollow.example.org",
            Port = 4000,
            Accent = TerminalColor.FromRgb(0x80, 0x80, 0x80),
        });
        return config;
    }

    /// <summary>Every distinct link target in the given rail markup, in the order the rail draws them.</summary>
    private static List<string> Targets(IEnumerable<string> lines)
    {
        var targets = new List<string>();
        foreach (var line in lines)
        {
            MarkupParser.Parse(line, SharpConsoleUI.Color.White, SharpConsoleUI.Color.Black, out var links);
            foreach (var link in links)
            {
                if (!targets.Contains(link.Url, StringComparer.Ordinal))
                {
                    targets.Add(link.Url);
                }
            }
        }

        return targets;
    }

    private static int RowIndex(SharpMUTermApp app, string text)
    {
        var lines = app.RailLines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(text, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"the rail has no row containing '{text}': {string.Join(" / ", lines)}");
    }

    /// <summary>The sidebar's column count, the way the layout derives it: widest visible row plus a margin.</summary>
    private static int RailColumns(SharpMUTermApp app) =>
        Math.Clamp(app.RailLines.Max(SharpMUTermApp.MarkupWidth) + 2, 16, 44);

    /// <summary>
    /// Clicks the middle of the rail row containing <paramref name="text"/>. The rail is the first
    /// control in the workspace row, which sits directly under the one-row header — so its row
    /// <c>i</c> is screen row <c>i + 1</c>. If that ever stops holding, every test using this fails
    /// with the switch not happening, which is the right way to find out.
    /// </summary>
    private static void ClickRailRow(SharpMUTermApp app, string text)
    {
        var index = RowIndex(app, text);
        var column = SharpMUTermApp.MarkupWidth(app.RailLines[index]) / 2;
        if (!app.SimulateRailClick(column, index))
        {
            throw new InvalidOperationException(
                $"no link under the middle of rail row {index} ('{app.RailLines[index]}')");
        }
    }

    private static string SpawnTitle(string spawnWindowId) => spawnWindowId["spawn:".Length..];
}
