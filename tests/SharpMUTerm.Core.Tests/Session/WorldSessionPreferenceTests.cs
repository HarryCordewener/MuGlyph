using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Session;

/// <summary>
/// The F7/F8 preferences, asserted as <em>behaviour</em> rather than as stored values: each one is
/// flipped and the session's output (or its echo) has to change. Every case flips the setting on a
/// session that is already connected, because these objects are the live configuration the settings
/// screens edit in place — a preference read once at construction would pass a persistence test and
/// still need a restart to mean anything, which is exactly the failure these pin against.
/// </summary>
public class WorldSessionPreferenceTests
{
    private static WorldDefinition World() => new() { Name = "T", Host = "h", Port = 1, LocalEcho = true };

    private static (WorldSession Session, FakeTelnetSession Telnet) Create(
        WorldDefinition world,
        TextSettings? text = null,
        InputSettings? input = null,
        TriggerSet? set = null)
    {
        var telnet = new FakeTelnetSession();
        var session = new WorldSession(
            world,
            triggerSets: set is null ? null : new[] { set },
            sessionFactory: _ => telnet,
            text: text,
            input: input);
        return (session, telnet);
    }

    // ---- F7: strip incoming ANSI colour ----

    [Test]
    public async Task StripIncomingColour_Off_KeepsWhatTheServerSent()
    {
        var (session, telnet) = Create(World(), new TextSettings { StripIncomingColour = false });
        await session.ConnectAsync();

        telnet.EmitLine("\x1b[31mdanger\x1b[0m");

        var line = session.Scrollback.Snapshot().First(l => l.Text == "danger");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(1));
    }

    [Test]
    public async Task StripIncomingColour_On_RendersInboundLinesInTheDefaultStyle()
    {
        var (session, telnet) = Create(World(), new TextSettings { StripIncomingColour = true });
        await session.ConnectAsync();

        telnet.EmitLine("\x1b[31;44mdanger\x1b[0m");

        var line = session.Scrollback.Snapshot().First(l => l.Text == "danger");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.Default);
        await Assert.That(line.Spans[0].Style.Background).IsEqualTo(TerminalColor.Default);
    }

    /// <summary>
    /// Attributes are not colour: a server that has lost its palette still marks emphasis with bold,
    /// so stripping takes the two colours and leaves the rendition alone.
    /// </summary>
    [Test]
    public async Task StripIncomingColour_KeepsAttributes()
    {
        var (session, telnet) = Create(World(), new TextSettings { StripIncomingColour = true });
        await session.ConnectAsync();

        telnet.EmitLine("\x1b[1;31mshouting\x1b[0m");

        var line = session.Scrollback.Snapshot().First(l => l.Text == "shouting");
        await Assert.That(line.Spans[0].Style.HasAttribute(TextAttributes.Bold)).IsTrue();
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.Default);
    }

    [Test]
    public async Task StripIncomingColour_TakesEffectOnTheNextLine_NotTheNextSession()
    {
        var text = new TextSettings { StripIncomingColour = false };
        var (session, telnet) = Create(World(), text);
        await session.ConnectAsync();

        telnet.EmitLine("\x1b[31mbefore\x1b[0m");
        text.StripIncomingColour = true;
        telnet.EmitLine("\x1b[31mafter\x1b[0m");

        var lines = session.Scrollback.Snapshot();
        var before = lines.First(l => l.Text == "before");
        var after = lines.First(l => l.Text == "after");
        await Assert.That(before.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(1));
        await Assert.That(after.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.Default);
    }

    /// <summary>
    /// A trigger's highlight is this client's colour, not the server's, so it survives the strip — and
    /// it has to, because stripping runs before the engine does.
    /// </summary>
    [Test]
    public async Task StripIncomingColour_LeavesATriggerHighlightAlone()
    {
        var set = new TriggerSet();
        set.Triggers.Add(new Trigger
        {
            Pattern = "tell",
            Actions = new TriggerActions { HighlightForeground = TerminalColor.FromIndex(14) },
        });
        var (session, telnet) = Create(World(), new TextSettings { StripIncomingColour = true }, set: set);
        await session.ConnectAsync();

        telnet.EmitLine("\x1b[31mAnvil pages: tell me more\x1b[0m");

        var line = session.Scrollback.Snapshot().First(l => l.Text.Contains("tell me more"));
        await Assert.That(line.Spans.Any(s => s.Style.Foreground == TerminalColor.FromIndex(14))).IsTrue();
        await Assert.That(line.Spans.Any(s => s.Style.Foreground == TerminalColor.FromIndex(1))).IsFalse();
    }

    // ---- F7: emoji substitution ----

    private static WorldDefinition EmojiWorld()
    {
        var world = World();
        world.Emoji.Enabled = true;
        return world;
    }

    [Test]
    public async Task EmojiSubstitution_On_SubstitutesForAWorldThatOptedIn()
    {
        var (session, telnet) = Create(EmojiWorld(), new TextSettings { EmojiSubstitution = true });
        await session.ConnectAsync();

        telnet.EmitLine("well done :smile:");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text.Contains(":smile:"))).IsFalse();
    }

    [Test]
    public async Task EmojiSubstitution_Off_IsTheAppWideOffSwitch()
    {
        var text = new TextSettings { EmojiSubstitution = true };
        var (session, telnet) = Create(EmojiWorld(), text);
        await session.ConnectAsync();

        text.EmojiSubstitution = false;
        telnet.EmitLine("well done :smile:");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text.Contains(":smile:"))).IsTrue();
    }

    // ---- F8: local echo ----

    [Test]
    public async Task LocalEcho_On_EchoesTypedCommands()
    {
        var (session, _) = Create(World(), input: new InputSettings { LocalEcho = true });
        await session.ConnectAsync();

        await session.SendUserInputAsync("look");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text == "look")).IsTrue();
    }

    [Test]
    public async Task LocalEcho_Off_StopsTheEchoOnTheNextCommand()
    {
        var input = new InputSettings { LocalEcho = true };
        var (session, telnet) = Create(World(), input: input);
        await session.ConnectAsync();

        await session.SendUserInputAsync("look");
        input.LocalEcho = false;
        await session.SendUserInputAsync("score");

        var lines = session.Scrollback.Snapshot();
        await Assert.That(lines.Any(l => l.Text == "look")).IsTrue();
        await Assert.That(lines.Any(l => l.Text == "score")).IsFalse();

        // Only the echo stops — both commands still reach the server.
        await Assert.That(telnet.SentLines).Contains("look");
        await Assert.That(telnet.SentLines).Contains("score");
    }

    /// <summary>
    /// Two switches, both of which have to be on. A world that echoes for itself keeps its own
    /// <c>LocalEcho = false</c> whatever F8 says, so turning the app-wide one on cannot start
    /// double-echoing that world.
    /// </summary>
    [Test]
    public async Task LocalEcho_TheWorldsOwnSwitchStillWins()
    {
        var world = World();
        world.LocalEcho = false;
        var (session, _) = Create(world, input: new InputSettings { LocalEcho = true });
        await session.ConnectAsync();

        await session.SendUserInputAsync("look");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text == "look")).IsFalse();
    }
}
