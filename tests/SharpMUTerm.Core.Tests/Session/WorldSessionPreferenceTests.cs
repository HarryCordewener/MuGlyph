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

    // ---- F7: tab width ----

    /// <summary>
    /// A tab from the server reaches scrollback as spaces. Asserted end to end rather than on
    /// <c>ExpandTabs</c> alone, because the unit is only worth anything if the session actually calls it.
    /// </summary>
    [Test]
    public async Task TabWidth_ExpandsATabInServerOutput()
    {
        var (session, telnet) = Create(World(), new TextSettings());
        await session.ConnectAsync();

        telnet.EmitLine("name\tvalue");

        var line = session.Scrollback.Snapshot().First(l => l.Text.StartsWith("name", StringComparison.Ordinal));
        await Assert.That(line.Text).IsEqualTo("name    value");
        await Assert.That(line.Text).DoesNotContain("\t");
    }

    /// <summary>
    /// Live, like every other preference here: the width is read per line, so a session already
    /// connected picks up the change on its next line rather than on a reconnect.
    /// </summary>
    [Test]
    public async Task TabWidth_TakesEffectOnTheNextLine_NotTheNextSession()
    {
        var text = new TextSettings { TabWidth = 2 };
        var (session, telnet) = Create(World(), text);
        await session.ConnectAsync();

        telnet.EmitLine("a\tb");
        await Assert.That(session.Scrollback.Snapshot().First(l => l.Text.StartsWith('a')).Text).IsEqualTo("a  b");

        text.TabWidth = 8;
        telnet.EmitLine("c\td");
        await Assert.That(session.Scrollback.Snapshot().First(l => l.Text.StartsWith('c')).Text).IsEqualTo("c        d");
    }

    /// <summary>
    /// <c>config.json</c> is hand-edited, so a nonsense width must not reach <c>ExpandTabs</c> and throw
    /// out of the read loop on an ordinary line of output. It is clamped at the point of use.
    /// </summary>
    [Test]
    [Arguments(-5, "ab")]
    [Arguments(0, "ab")]
    [Arguments(999, "a                b")]
    public async Task TabWidth_OutOfRange_IsClampedRatherThanThrowing(int width, string expected)
    {
        var (session, telnet) = Create(World(), new TextSettings { TabWidth = width });
        await session.ConnectAsync();

        telnet.EmitLine("a\tb");

        await Assert.That(session.Scrollback.Snapshot().First(l => l.Text.StartsWith('a')).Text).IsEqualTo(expected);
    }

    /// <summary>
    /// The expansion runs before the trigger engine, so a pattern matches the spaces the reader sees
    /// rather than a tab they have no way to know is there.
    /// </summary>
    [Test]
    public async Task TabWidth_ExpandsBeforeTriggersSeeTheLine()
    {
        var set = new TriggerSet
        {
            Name = "t",
            Triggers =
            {
                new Trigger { Pattern = "name    value", Actions = new TriggerActions { Gag = true } },
            },
        };

        var (session, telnet) = Create(World(), new TextSettings(), set: set);
        await session.ConnectAsync();

        telnet.EmitLine("name\tvalue");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text.Contains("value", StringComparison.Ordinal)))
            .IsFalse()
            .Because("the rule matched the expanded text, so the line was gagged");
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
