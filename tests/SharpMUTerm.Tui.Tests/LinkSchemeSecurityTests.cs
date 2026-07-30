using SharpConsoleUI.Drivers;
using SharpConsoleUI.Parsing;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What a <em>world</em> can make this client do with a link, driven the whole distance: raw MXP/Pueblo
/// bytes handed to a connected session, through its configured parser, <see cref="MarkupFormatter"/>, the
/// framework's markup parse and its <see cref="LinkUrl"/> unescape, a real mouse click on the painted
/// pane, and out the other side as something that either did or did not reach the transport.
/// <para>
/// The defect these were written for: the formatter emitted a hyperlink's <c>href</c> verbatim into the
/// <c>[link=…]</c> payload, so <c>&lt;A HREF="mux:send:@shutdown"&gt;</c> produced a payload
/// byte-identical to a genuine <c>&lt;SEND&gt;</c> and the click handler wrote it to the wire — a world
/// running a command as the user from an ordinary hyperlink. Nothing tested the two halves as a pair,
/// which is how it survived: the formatter's tests asserted the payload it built and the handler's tests
/// fed it payloads by hand, so neither ever asked whether a server could write one.
/// </para>
/// <para>
/// The sessions are <strong>connected</strong> on purpose. <see cref="WorldSession.SendRawAsync"/>
/// returns immediately when there is no live transport, so "nothing reached the wire" asserted against an
/// unconnected session is true no matter what the code under test does.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class LinkSchemeSecurityTests
{
    private const int Width = 160;
    private const int Height = 40;
    private const string MainWindow = "main";

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    // ---- A world cannot forge a privileged scheme -------------------------------------------

    /// <summary>
    /// The regression test. An MXP anchor whose <c>href</c> spells out the send scheme is a
    /// <em>hyperlink</em> — MXP keeps <c>&lt;A&gt;</c> and <c>&lt;SEND&gt;</c> as separate tags for exactly
    /// this reason — so clicking it must not put <c>@shutdown</c> on the wire.
    /// </summary>
    [Test]
    public async Task AnMxpAnchorSpellingTheSendScheme_SendsNothing()
    {
        var world = await Connected(ContentFormat.Mxp);
        world.Receive("<A HREF=\"mux:send:@shutdown\">click me</A>\n");

        await Assert.That(ClickLink(world, "click me")).IsTrue(); // the link really is clickable

        await Assert.That(world.Telnet.Lines).IsEmpty();
    }

    /// <summary>Pueblo's anchor is the same tag with the same hole, and gets the same answer.</summary>
    [Test]
    public async Task APuebloAnchorSpellingTheSendScheme_SendsNothing()
    {
        var world = await Connected(ContentFormat.Pueblo);
        world.Receive("<A HREF=\"mux:send:@shutdown\">click me</A>\n");

        await Assert.That(ClickLink(world, "click me")).IsTrue();

        await Assert.That(world.Telnet.Lines).IsEmpty();
    }

    /// <summary>
    /// The prompt scheme is the same forgery with a quieter payoff: text of the world's choosing sitting
    /// on the user's command line, one ⏎ from being sent. MXP's <c>PROMPT</c> is a keyword on
    /// <c>&lt;SEND&gt;</c>, so an anchor must not reach it either.
    /// </summary>
    [Test]
    public async Task AnMxpAnchorSpellingThePromptScheme_DoesNotTouchTheCommandLine()
    {
        var world = await Connected(ContentFormat.Mxp);
        world.Receive("<A HREF=\"mux:prompt:@shutdown\">click me</A>\n");

        await Assert.That(ClickLink(world, "click me")).IsTrue();

        await Assert.That(world.App.ArmedInputText).IsEmpty();
        await Assert.That(world.Telnet.Lines).IsEmpty();
    }

    /// <summary>
    /// What the forged link becomes instead: a hyperlink whose URL happens to read <c>mux:send:…</c>, so
    /// the click goes to the web view and <c>WebPageFetcher</c>'s http(s) gate refuses it there. A world
    /// that lies about what its link is gets a page it cannot load, not a command run as the user.
    /// </summary>
    [Test]
    public async Task AForgedScheme_IsTreatedAsTheHyperlinkItIs()
    {
        var world = await Connected(ContentFormat.Mxp);
        world.Receive("<A HREF=\"mux:send:@shutdown\">click me</A>\n");

        ClickLink(world, "click me");

        await Assert.That(string.Join("\n", world.App.PaneLines(MainWindow)))
            .Contains("Opening mux:send:@shutdown in the web view");
        await Assert.That(world.Telnet.Lines).IsEmpty();
    }

    /// <summary>
    /// A world can still put a bracket in a URL, and must not be able to close the <c>[link=…]</c> tag
    /// with it and inject markup of its own into the pane. This defence already existed and has to
    /// survive the fix, so it is pinned on the payload and on the rendered link's extent.
    /// </summary>
    [Test]
    public async Task AnHrefFullOfMarkupMetacharacters_CannotCloseTheTag()
    {
        const string href = "https://x/?a=][bold red]owned[/";
        var world = await Connected(ContentFormat.Mxp);
        world.Receive($"<A HREF=\"{href}\">click me</A>\n");

        var link = LinkFor(world, "click me");

        // The payload arrives whole, and the link covers the anchor text and nothing else: an injected
        // [bold red] would have ended the tag early and left "owned" outside the link as visible text.
        await Assert.That(link.Url).IsEqualTo(LinkPayload.WebScheme + href);
        await Assert.That(link.Text).IsEqualTo("click me");
        await Assert.That(LinkPayload.Parse(link.Url).Action).IsEqualTo(LinkAction.Web);
        await Assert.That(string.Join("\n", world.App.PaneLines(MainWindow))).DoesNotContain("[bold red]");
    }

    // ---- What must still work ---------------------------------------------------------------

    /// <summary>
    /// The thing this must not break: <c>&lt;SEND&gt;</c> is MXP's clickable command and still sends. A
    /// fix that made every link inert would pass every test above.
    /// </summary>
    [Test]
    public async Task ALegitimateSendLink_StillSends()
    {
        var world = await Connected(ContentFormat.Mxp);
        world.Receive("<SEND HREF=\"look\">the door</SEND>\n");

        await Assert.That(ClickLink(world, "the door")).IsTrue();

        await Assert.That(world.Telnet.Lines).IsEquivalentTo(new[] { "look" });
    }

    /// <summary>And <c>&lt;SEND PROMPT&gt;</c> still fills in the command line without sending.</summary>
    [Test]
    public async Task ALegitimateSendPromptLink_StillFillsTheCommandLine()
    {
        var world = await Connected(ContentFormat.Mxp);
        world.Receive("<SEND HREF=\"cast spell\" PROMPT>the wand</SEND>\n");

        await Assert.That(ClickLink(world, "the wand")).IsTrue();

        await Assert.That(world.App.ArmedInputText).IsEqualTo("cast spell");
        await Assert.That(world.Telnet.Lines).IsEmpty();
    }

    /// <summary>
    /// A real hyperlink still opens the web view, with the URL intact. Asserted on the transcript line the
    /// client writes as it starts the fetch, because the fetch itself is a socket — the claim is that the
    /// click routed to the web action carrying the whole URL, not what a server answered.
    /// </summary>
    [Test]
    public async Task ALegitimateHyperlink_StillOpensTheWebView()
    {
        var world = await Connected(ContentFormat.Mxp);
        world.Receive("<A HREF=\"http://127.0.0.1:1/page?q=1&r=2\">the site</A>\n");

        await Assert.That(ClickLink(world, "the site")).IsTrue();

        await Assert.That(string.Join("\n", world.App.PaneLines(MainWindow)))
            .Contains("Opening http://127.0.0.1:1/page?q=1&r=2 in the web view");
        await Assert.That(world.Telnet.Lines).IsEmpty(); // a hyperlink is never a command
    }

    // ---- The URL round trip -----------------------------------------------------------------

    /// <summary>
    /// A hyperlink target now travels inside a scheme, so it has to be escaped — and it has to come back
    /// out byte-for-byte. These are the cases where a percent-encoding scheme goes wrong: a literal
    /// <c>%</c> that is not an escape, an already-encoded <c>%20</c> that must survive undecoded, a
    /// <c>]</c> that must not end the markup tag, Unicode, and a bare fragment of metacharacters.
    /// <para>
    /// Driven through <see cref="LinkPayload.Delivered"/>, which is the framework's own unescape — the
    /// step that sits between the formatter and the click. A round trip asserted without it would be
    /// checking a chain the app does not use.
    /// </para>
    /// </summary>
    [Test]
    [Arguments("https://example.org/")]
    [Arguments("https://example.org/search?q=a+b&lang=en&x=1#frag")]
    [Arguments("https://example.org/a%20b")]
    [Arguments("https://example.org/100%25?pct=%&raw=%zz")]
    [Arguments("https://example.org/path/with]bracket[and%5Dencoded")]
    [Arguments("https://example.org/wiki/Café_münchen?q=日本語")]
    [Arguments("https://example.org/?a=1&a=2&a=3#=&%")]
    public async Task AHyperlinkTarget_SurvivesTheRoundTripIntact(string url)
    {
        var emitted = LinkPayload.For(SpanInteraction.Link(url))!;

        await Assert.That(emitted).DoesNotContain("]"); // or a server closes the [link=…] tag early
        await Assert.That(LinkPayload.Delivered(emitted)).IsEqualTo(new LinkPayload(LinkAction.Web, url));
    }

    /// <summary>A URL far longer than any escaping helper's historical length limit still round-trips.</summary>
    [Test]
    public async Task AVeryLongHyperlinkTarget_SurvivesTheRoundTripIntact()
    {
        var url = "https://example.org/?q=" + string.Concat(Enumerable.Repeat("a%20b]c&d=", 8000));

        var emitted = LinkPayload.For(SpanInteraction.Link(url))!;

        await Assert.That(emitted).DoesNotContain("]");
        await Assert.That(LinkPayload.Delivered(emitted)).IsEqualTo(new LinkPayload(LinkAction.Web, url));
    }

    /// <summary>
    /// The same round trip for the two command schemes, over the characters a command carries. A command
    /// is text a user reads and a world executes, so a mangled one is a wrong command sent to a world.
    /// </summary>
    [Test]
    [Arguments("look")]
    [Arguments("say 100% sure")]
    [Arguments("say %41 is not A")]
    [Arguments("page bob=hello [ok] there")]
    [Arguments("+finger Café")]
    public async Task ACommandTarget_SurvivesTheRoundTripIntact(string command)
    {
        var send = LinkPayload.For(SpanInteraction.Command(command))!;
        var prompt = LinkPayload.For(SpanInteraction.Command(command, promptOnly: true))!;

        await Assert.That(send).DoesNotContain("]");
        await Assert.That(prompt).DoesNotContain("]");
        await Assert.That(LinkPayload.Delivered(send)).IsEqualTo(new LinkPayload(LinkAction.Send, command));
        await Assert.That(LinkPayload.Delivered(prompt)).IsEqualTo(new LinkPayload(LinkAction.Prompt, command));
    }

    /// <summary>
    /// The property the fix rests on, stated directly: the three namespaces are disjoint, so no target a
    /// world chooses can make one kind arrive as another. The interesting inputs are the schemes
    /// themselves and the client's other namespaces — the payloads a world would want to forge.
    /// </summary>
    [Test]
    [Arguments("mux:send:@shutdown")]
    [Arguments("mux:prompt:@shutdown")]
    [Arguments("mux:web:https://example.org")]
    [Arguments("sharpmuterm-menu:toggle")]
    [Arguments("char:Aetherfall.Rookery")]
    [Arguments("win:main")]
    public async Task NoTargetMakesOneKindArriveAsAnother(string target)
    {
        await Assert.That(LinkPayload.Delivered(LinkPayload.For(SpanInteraction.Link(target))!))
            .IsEqualTo(new LinkPayload(LinkAction.Web, target));
        await Assert.That(LinkPayload.Delivered(LinkPayload.For(SpanInteraction.Command(target))!))
            .IsEqualTo(new LinkPayload(LinkAction.Send, target));
        await Assert.That(LinkPayload.Delivered(LinkPayload.For(SpanInteraction.Command(target, promptOnly: true))!))
            .IsEqualTo(new LinkPayload(LinkAction.Prompt, target));
    }

    /// <summary>An untagged payload has no action. There is no "probably a URL" arm to fall into.</summary>
    [Test]
    [Arguments("https://example.org")]
    [Arguments("sharpmuterm-menu:toggle")]
    [Arguments("char:Aetherfall.Rookery")]
    [Arguments("win:main")]
    [Arguments("rail:nonesuch")]
    [Arguments("")]
    [Arguments("mux:")]
    [Arguments("mux:send")]
    [Arguments("MUX:SEND:look")]
    public async Task AnUntaggedPayload_HasNoAction(string url) =>
        await Assert.That(LinkPayload.Parse(url)).IsEqualTo(LinkPayload.None);

    // ---- A link acts on the world it was clicked in -----------------------------------------

    /// <summary>
    /// A link belongs to the window it was drawn in, not to whichever character is focused. Every pane,
    /// spawn window and frozen region shared one link closure with no owning-window context and the
    /// handler acted on the active session, so a click in world A's visible-but-unfocused pane sent A's
    /// command to world B — misdelivery, the same shape as a draft following the user to another window.
    /// <para>
    /// Driven through a split, because that is the situation in which the bug bites: a background
    /// window's pane has to be on screen for anyone to click it. Both directions are asserted — the
    /// clicked world receives the command <em>and</em> the focused one receives nothing, since a handler
    /// that sent to neither would pass a one-sided test.
    /// </para>
    /// </summary>
    [Test]
    public async Task ALinkClickedInAnUnfocusedPane_SendsToThatPanesWorld()
    {
        var two = await TwoConnectedWorlds();

        // Quiet's window carries the link; Loud is the focused one, in the other half of the split.
        Receive(two, two.Quiet, "<SEND HREF=\"look\">the door</SEND>\n");
        two.App.RenderNextFrame();
        await Assert.That(two.App.DispatchCommand("layout:split-right")).IsTrue();
        two.App.RenderNextFrame();
        await Assert.That(two.App.ActiveWindowId()).IsEqualTo(two.LoudWindow);

        await Assert.That(ClickLinkIn(two.App, two.QuietWindow, "the door")).IsTrue();

        await Assert.That(two.Quiet.Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(two.Loud.Lines).IsEmpty();
    }

    /// <summary>
    /// A spawn window is not any session's own output window, so the owning world is resolved from the
    /// workspace's record of who opened it. Pinned because that resolution is a second path and an
    /// unowned spawn window would have sent nowhere — a link that quietly does nothing is the failure
    /// mode a "nothing reached the wire" test cannot tell from a fix.
    /// </summary>
    [Test]
    public async Task ALinkInASpawnWindow_SendsToTheWorldWhoseTriggerOpenedIt()
    {
        var two = await TwoConnectedWorlds();

        // Quiet's own trigger routes the line to a spawn window, which is therefore Quiet's.
        Receive(two, two.Quiet, "[Chat] <SEND HREF=\"look\">the door</SEND>\n");
        two.App.RenderNextFrame();

        var spawn = two.App.WindowIds().Single(id => id.StartsWith("spawn:", StringComparison.Ordinal));
        await Assert.That(two.App.ActiveWindowId()).IsEqualTo(two.LoudWindow); // spawns open in the background

        two.App.OnLinkClicked(spawn, LinkPayload.SendScheme + "look");

        await Assert.That(two.Quiet.Lines).IsEquivalentTo(new[] { "look" });
        await Assert.That(two.Loud.Lines).IsEmpty();
    }

    /// <summary>
    /// A window belonging to no world — the web view is the one that exists — has nowhere to send, and
    /// says so rather than falling back on the focused character. That fallback is the bug this section
    /// is about; keeping it "just for the odd case" would keep the bug for the odd case.
    /// </summary>
    [Test]
    public async Task ASendLinkInAWindowWithNoWorld_IsRefusedRatherThanRedirected()
    {
        var world = await Connected(ContentFormat.Mxp);

        world.App.OnLinkClicked("web", LinkPayload.SendScheme + "@shutdown");

        await Assert.That(world.Telnet.Lines).IsEmpty();
        await Assert.That(world.App.StatusMarkup).Contains("belongs to no connection");
    }

    // ---- Harness ---------------------------------------------------------------------------

    /// <summary>One world with a recording transport under it, connected, printing into a window.</summary>
    private sealed record Wired(SharpMUTermApp App, RecordingTelnetSession Telnet, string WindowId)
    {
        /// <summary>Delivers server text and renders, so the pane's links have hit-test geometry.</summary>
        public void Receive(string text)
        {
            Telnet.Receive(text);
            App.RenderNextFrame();
        }
    }

    /// <summary>
    /// An app holding one connected world whose content format is <paramref name="format"/>, bound to the
    /// main window. The connection is the point — see the type's remarks.
    /// </summary>
    private static async Task<Wired> Connected(ContentFormat format)
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration();
        config.Worlds.Add(World("Hostile", format));

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        var telnet = new RecordingTelnetSession();
        app.TelnetFactory = _ => telnet;
        var session = app.BindWorldWithoutConnecting(config.Worlds[0]);
        await session.ConnectAsync();
        app.RenderNextFrame();
        return new Wired(app, telnet, MainWindow);
    }

    /// <summary>
    /// Two connected worlds in one app, each in its own window, with Loud's the focused one. Both
    /// transports are held so a test can assert which of the two a command reached — and, just as
    /// importantly, which it did not.
    /// </summary>
    private sealed record TwoWorlds(
        SharpMUTermApp App,
        RecordingTelnetSession Quiet,
        RecordingTelnetSession Loud,
        string QuietWindow,
        string LoudWindow);

    /// <summary>
    /// Two characters in two worlds, each with a connected recording transport, opened through the app's
    /// real character-switching command so each gets its own window the way the shell gives it one. Quiet
    /// carries a capture rule, so a <c>[Chat]</c> line of its own opens a spawn window it owns.
    /// </summary>
    private static async Task<TwoWorlds> TwoConnectedWorlds()
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration();
        config.TriggerSets.Add(new TriggerSet
        {
            Name = "chat",
            Triggers =
            {
                new Trigger { Name = "chat", Pattern = @"^\[Chat\]", Actions = new TriggerActions { SpawnTarget = "Chat" } },
            },
        });

        var quietWorld = World("Quiet", ContentFormat.Mxp);
        quietWorld.Characters.Add(new CharacterDefinition
        {
            Name = "Ann",
            Logging = new LoggingSettings(),
            TriggerSets = { "chat" },
        });

        var loudWorld = World("Loud", ContentFormat.Mxp);
        loudWorld.Characters.Add(new CharacterDefinition { Name = "Bob", Logging = new LoggingSettings() });

        config.Worlds.Add(quietWorld);
        config.Worlds.Add(loudWorld);

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        var quiet = new RecordingTelnetSession();
        var loud = new RecordingTelnetSession();

        // Keyed on the host so each session's writes are attributable — the whole suite turns on which
        // transport a command reached.
        app.TelnetFactory = options =>
            options.Host.StartsWith("quiet", StringComparison.Ordinal) ? quiet : loud;

        var quietWindow = await Open(app, "Quiet.Ann");
        var loudWindow = await Open(app, "Loud.Bob");
        app.RenderNextFrame();

        return new TwoWorlds(app, quiet, loud, quietWindow, loudWindow);
    }

    /// <summary>Delivers server text to one of a pair's transports and renders a settling frame.</summary>
    private static void Receive(TwoWorlds two, RecordingTelnetSession telnet, string text)
    {
        telnet.Receive(text);
        two.App.RenderNextFrame();
    }

    /// <summary>Switches to a character the way ⌃P and the rail do, connects it, and reports its window.</summary>
    private static async Task<string> Open(SharpMUTermApp app, string sessionKey)
    {
        if (!app.DispatchCommand(CommandIds.Character(sessionKey)))
        {
            throw new InvalidOperationException($"the app would not switch to {sessionKey}");
        }

        await app.FindSession(sessionKey)!.ConnectAsync();
        return app.ActiveWindowId();
    }

    private static WorldDefinition World(string name, ContentFormat format) => new()
    {
        Name = name,
        Host = $"{name.ToLowerInvariant()}.example.org",
        Port = 4000,
        ContentFormat = format,
    };

    // ---- Reading and clicking the links the app really drew ---------------------------------

    private static bool ClickLink(Wired world, string text) => ClickLinkIn(world.App, world.WindowId, text);

    /// <summary>
    /// Clicks the first cell of the link whose visible text contains <paramref name="text"/>. Both the row
    /// and the column come from the markup the app drew, parsed by the framework's own parser — nothing
    /// about the payload or its position is written down here.
    /// </summary>
    private static bool ClickLinkIn(SharpMUTermApp app, string windowId, string text)
    {
        var lines = app.PaneLines(windowId);
        for (var row = 0; row < lines.Count; row++)
        {
            if (LinkOver(lines[row], text) is { } link)
            {
                return app.SimulatePaneClick(windowId, link.StartCol, row);
            }
        }

        throw new InvalidOperationException($"no link reading '{text}' in: {string.Join(" / ", lines)}");
    }

    private static LinkSpan LinkFor(Wired world, string text)
    {
        foreach (var line in world.App.PaneLines(world.WindowId))
        {
            if (LinkOver(line, text) is { } link)
            {
                return link;
            }
        }

        throw new InvalidOperationException($"no link reading '{text}' in {world.WindowId}");
    }

    private static LinkSpan? LinkOver(string markup, string text)
    {
        MarkupParser.Parse(markup, SharpConsoleUI.Color.White, SharpConsoleUI.Color.Black, out var links);
        foreach (var link in links)
        {
            if (link.Text.Contains(text, StringComparison.Ordinal))
            {
                return link;
            }
        }

        return null;
    }
}
