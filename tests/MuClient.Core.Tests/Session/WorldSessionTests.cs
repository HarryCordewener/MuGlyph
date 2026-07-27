using MuClient.Core.Automation;
using MuClient.Core.Configuration;
using MuClient.Core.Session;
using MuClient.Core.Telnet;
using MuClient.Core.Text;

namespace MuClient.Core.Tests.Session;

public class WorldSessionTests
{
    private static (WorldSession session, FakeTelnetSession telnet) Create(WorldDefinition world)
    {
        var telnet = new FakeTelnetSession();
        var session = new WorldSession(world, _ => telnet);
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
        var world = World();
        world.Triggers.Add(new Trigger { Pattern = "secret", Actions = new TriggerActions { Gag = true } });
        var (session, telnet) = Create(world);
        await session.ConnectAsync();

        telnet.EmitLine("a secret message");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text.Contains("secret message"))).IsFalse();
    }

    [Test]
    public async Task Trigger_Response_IsSentToServer()
    {
        var world = World();
        world.Triggers.Add(new Trigger
        {
            Pattern = @"^(\w+) waves",
            Actions = new TriggerActions { SendResponse = "wave $1" },
        });
        var (session, telnet) = Create(world);
        await session.ConnectAsync();

        telnet.EmitLine("Gandalf waves");

        await Assert.That(telnet.SentLines).Contains("wave Gandalf");
    }

    [Test]
    public async Task Trigger_Spawn_RoutesLineToSpawnEvent()
    {
        var world = World();
        world.Triggers.Add(new Trigger { Pattern = @"\[chat\]", Actions = new TriggerActions { SpawnTarget = "Chat" } });
        var (session, telnet) = Create(world);
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
        var world = World();
        world.Aliases.Add(new Alias { Pattern = "^gt (.+)", Substitution = "grouptell $1" });
        var (session, telnet) = Create(world);
        await session.ConnectAsync();

        await session.SendUserInputAsync("gt hello");

        await Assert.That(telnet.SentLines).Contains("grouptell hello");
        await Assert.That(telnet.SentLines).DoesNotContain("gt hello");
    }

    [Test]
    public async Task Macro_KeyResolvesAndSends()
    {
        var world = World();
        world.Macros.Add(new Macro { Key = "Ctrl+F1", Command = "north" });
        var (session, telnet) = Create(world);
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
