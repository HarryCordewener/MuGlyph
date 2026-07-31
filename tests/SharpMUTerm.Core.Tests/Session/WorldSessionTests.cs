using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Session;

public class WorldSessionTests
{
    private static (WorldSession session, FakeTelnetSession telnet) Create(
        WorldDefinition world,
        TriggerSet? set = null)
    {
        var telnet = new FakeTelnetSession();
        var sets = set is null ? null : new[] { set };
        var session = new WorldSession(world, triggerSets: sets, sessionFactory: _ => telnet);
        return (session, telnet);
    }

    private static WorldDefinition World() => new() { Name = "T", Host = "h", Port = 1, LocalEcho = true };

    [Test]
    public async Task OutputLine_IsAppendedToScrollbackAndRaisesEvent()
    {
        var (session, telnet) = Create(World());
        StyledLine? printed = null;
        session.LinePrinted += (_, l) => printed = l;
        await session.ConnectAsync();

        telnet.EmitLine("You see a troll.");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text == "You see a troll.")).IsTrue();
        await Assert.That(printed).IsNotNull();
    }

    [Test]
    public async Task EmptyOutputLine_IsAppendedAsBlankLine()
    {
        var (session, telnet) = Create(World());
        await session.ConnectAsync();
        var before = session.Scrollback.Snapshot().Count;

        telnet.EmitLine(string.Empty);

        var after = session.Scrollback.Snapshot();
        await Assert.That(after.Count).IsEqualTo(before + 1);
        await Assert.That(after[^1].IsEmpty).IsTrue();
    }

    [Test]
    public async Task SameFrame_OutputPreservesAnEmptyMiddleLine()
    {
        var (session, telnet) = Create(World());
        await session.ConnectAsync();
        var before = session.Scrollback.Snapshot().Count;

        telnet.EmitLine("Line one\n\nThis line three, previous line was empty, this is valid output. It came from the same received frame.");

        var after = session.Scrollback.Snapshot();
        await Assert.That(after.Count).IsEqualTo(before + 3);
        await Assert.That(after[before].Text).IsEqualTo("Line one");
        await Assert.That(after[before + 1].IsEmpty).IsTrue();
        await Assert.That(after[before + 2].Text).IsEqualTo("This line three, previous line was empty, this is valid output. It came from the same received frame.");
    }

    [Test]
    public async Task AnsiColor_InOutput_IsParsedIntoStyledSpans()
    {
        var (session, telnet) = Create(World());
        await session.ConnectAsync();

        telnet.EmitLine("\x1b[31mdanger\x1b[0m");

        var line = session.Scrollback.Snapshot().First(l => l.Text == "danger");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(1));
    }

    [Test]
    public async Task Trigger_Gag_SuppressesLineFromScrollback()
    {
        var set = new TriggerSet();
        set.Triggers.Add(new Trigger { Pattern = "secret", Actions = new TriggerActions { Gag = true } });
        var (session, telnet) = Create(World(), set);
        await session.ConnectAsync();

        telnet.EmitLine("a secret message");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text.Contains("secret message"))).IsFalse();
    }

    [Test]
    public async Task Trigger_Response_IsSentToServer()
    {
        var set = new TriggerSet();
        set.Triggers.Add(new Trigger
        {
            Pattern = @"^(\w+) waves",
            Actions = new TriggerActions { SendResponse = "wave $1" },
        });
        var (session, telnet) = Create(World(), set);
        await session.ConnectAsync();

        telnet.EmitLine("Gandalf waves");

        await Assert.That(telnet.SentLines).Contains("wave Gandalf");
    }

    [Test]
    public async Task Trigger_Spawn_RoutesLineToSpawnEvent()
    {
        var set = new TriggerSet();
        set.Triggers.Add(new Trigger { Pattern = @"\[chat\]", Actions = new TriggerActions { SpawnTarget = "Chat" } });
        var (session, telnet) = Create(World(), set);
        SpawnLineEventArgs? spawned = null;
        session.SpawnLine += (_, e) => spawned = e;
        await session.ConnectAsync();

        telnet.EmitLine("[chat] hello");

        await Assert.That(spawned).IsNotNull();
        await Assert.That(spawned!.Target).IsEqualTo("Chat");
    }

    [Test]
    public async Task Prompt_UpdatesCurrentPrompt_WithoutScrollback()
    {
        var (session, telnet) = Create(World());
        StyledLine? promptEvt = null;
        session.PromptChanged += (_, p) => promptEvt = p;
        await session.ConnectAsync();

        telnet.EmitPrompt("HP:100 >");

        await Assert.That(session.CurrentPrompt).IsNotNull();
        await Assert.That(session.CurrentPrompt!.Text).IsEqualTo("HP:100 >");
        await Assert.That(promptEvt).IsNotNull();
        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text == "HP:100 >")).IsFalse();
    }

    [Test]
    public async Task UserInput_IsEchoedAndSent()
    {
        var (session, telnet) = Create(World());
        await session.ConnectAsync();

        await session.SendUserInputAsync("look");

        await Assert.That(telnet.SentLines).Contains("look");
        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text == "look")).IsTrue();
    }

    [Test]
    public async Task UserInput_AliasIsExpandedBeforeSend()
    {
        var set = new TriggerSet();
        set.Aliases.Add(new Alias { Pattern = "^gt (.+)", Substitution = "grouptell $1" });
        var (session, telnet) = Create(World(), set);
        await session.ConnectAsync();

        await session.SendUserInputAsync("gt hello");

        await Assert.That(telnet.SentLines).Contains("grouptell hello");
        await Assert.That(telnet.SentLines).DoesNotContain("gt hello");
    }

    [Test]
    public async Task Macro_KeyResolvesAndSends()
    {
        var set = new TriggerSet();
        set.Macros.Add(new Macro { Key = "Ctrl+F1", Command = "north" });
        var (session, telnet) = Create(World(), set);
        await session.ConnectAsync();

        var command = await session.HandleKeyAsync("Ctrl+F1");

        await Assert.That(command).IsEqualTo("north");
        await Assert.That(telnet.SentLines).Contains("north");
    }

    [Test]
    public async Task Gmcp_IsReRaised()
    {
        var (session, telnet) = Create(World());
        GmcpMessageEventArgs? gmcp = null;
        session.GmcpReceived += (_, e) => gmcp = e;
        await session.ConnectAsync();

        telnet.EmitGmcp("Char.Vitals", "{\"hp\":50}");

        await Assert.That(gmcp).IsNotNull();
        await Assert.That(gmcp!.Package).IsEqualTo("Char.Vitals");
    }

    /// <summary>
    /// A saved password is the whole configuration a login needs: no second flag, and the on-connect
    /// commands follow it.
    /// </summary>
    [Test]
    public async Task Character_WithASavedPassword_SendsConnectStringAndOnConnect()
    {
        var character = new CharacterDefinition
        {
            Name = "Wizard",
            Password = "swordfish",
            OnConnect = "look; who",
        };
        var telnet = new FakeTelnetSession();
        var session = new WorldSession(World(), character, sessionFactory: _ => telnet);

        await session.ConnectAsync();

        await Assert.That(telnet.SentLines).Contains("connect Wizard swordfish");
        await Assert.That(telnet.SentLines).Contains("look");
        await Assert.That(telnet.SentLines).Contains("who");
        await Assert.That(session.SessionKey).IsEqualTo("T.Wizard");
    }

    [Test]
    public async Task AnonymousSession_KeyIsWorldName_AndSendsNoLoginLine()
    {
        var (session, telnet) = Create(World());
        await session.ConnectAsync();

        await Assert.That(session.SessionKey).IsEqualTo("T");
        await Assert.That(telnet.SentLines).IsEmpty();
    }

    [Test]
    public async Task State_TransitionsToConnected()
    {
        var (session, _) = Create(World());
        var states = new List<ConnectionState>();
        session.StateChanged += (_, e) => states.Add(e.State);
        await session.ConnectAsync();

        await Assert.That(session.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(states).Contains(ConnectionState.Connecting);
        await Assert.That(states).Contains(ConnectionState.Connected);
    }
}
