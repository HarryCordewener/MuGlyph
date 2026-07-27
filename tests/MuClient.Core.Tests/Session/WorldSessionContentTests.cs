using MuClient.Core.Configuration;
using MuClient.Core.Session;
using MuClient.Core.Text;

namespace MuClient.Core.Tests.Session;

public class WorldSessionContentTests
{
    private static (WorldSession session, FakeTelnetSession telnet) Create(WorldDefinition world)
    {
        var telnet = new FakeTelnetSession();
        return (new WorldSession(world, _ => telnet), telnet);
    }

    private static StyledLine FindLine(WorldSession session, string text) =>
        session.Scrollback.Snapshot().First(l => l.Text == text);

    [Test]
    public async Task MxpContentFormat_ParsesTagsIntoStyledSpans()
    {
        var world = new WorldDefinition { Name = "M", Host = "h", Port = 1, ContentFormat = ContentFormat.Mxp };
        var (session, telnet) = Create(world);
        await session.ConnectAsync();

        telnet.EmitLine("<B>bold</B>");

        var line = FindLine(session, "bold");
        await Assert.That(line.Spans.Any(s => s.Style.HasAttribute(TextAttributes.Bold))).IsTrue();
    }

    [Test]
    public async Task MxpContentFormat_SendLinkBecomesInteractiveSpan()
    {
        var world = new WorldDefinition { Name = "M", Host = "h", Port = 1, ContentFormat = ContentFormat.Mxp };
        var (session, telnet) = Create(world);
        await session.ConnectAsync();

        telnet.EmitLine("<SEND HREF=\"look\">here</SEND>");

        var line = FindLine(session, "here");
        var span = line.Spans.First(s => s.Text.Contains("here"));
        await Assert.That(span.IsInteractive).IsTrue();
        await Assert.That(span.Interaction!.Kind).IsEqualTo(InteractionKind.SendCommand);
        await Assert.That(span.Interaction!.Target).IsEqualTo("look");
    }

    [Test]
    public async Task PuebloContentFormat_ParsesXchCmdLink()
    {
        var world = new WorldDefinition { Name = "P", Host = "h", Port = 1, ContentFormat = ContentFormat.Pueblo };
        var (session, telnet) = Create(world);
        await session.ConnectAsync();

        telnet.EmitLine("<A XCH_CMD=\"north\">go north</A>");

        var line = FindLine(session, "go north");
        var span = line.Spans.First(s => s.IsInteractive);
        await Assert.That(span.Interaction!.Kind).IsEqualTo(InteractionKind.SendCommand);
        await Assert.That(span.Interaction!.Target).IsEqualTo("north");
    }

    [Test]
    public async Task AnsiContentFormat_IsDefault()
    {
        var world = new WorldDefinition { Name = "A", Host = "h", Port = 1 };
        var (session, telnet) = Create(world);
        await session.ConnectAsync();

        telnet.EmitLine("\x1b[31mred\x1b[0m");

        var line = FindLine(session, "red");
        await Assert.That(line.Spans[0].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(1));
    }

    [Test]
    public async Task Emoji_SubstitutesInOutput_WhenEnabled()
    {
        var world = new WorldDefinition { Name = "E", Host = "h", Port = 1 };
        world.Emoji.Enabled = true;
        var (session, telnet) = Create(world);
        await session.ConnectAsync();

        telnet.EmitLine("greetings :fire:");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text.Contains("🔥"))).IsTrue();
    }

    [Test]
    public async Task Emoji_NotApplied_WhenDisabled()
    {
        var world = new WorldDefinition { Name = "E", Host = "h", Port = 1 };
        var (session, telnet) = Create(world);
        await session.ConnectAsync();

        telnet.EmitLine("greetings :fire:");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text.Contains(":fire:"))).IsTrue();
    }
}
