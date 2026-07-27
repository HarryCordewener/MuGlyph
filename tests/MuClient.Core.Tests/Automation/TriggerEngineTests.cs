using MuClient.Core.Automation;
using MuClient.Core.Text;

namespace MuClient.Core.Tests.Automation;

public class TriggerEngineTests
{
    private static StyledLine Line(string text) => StyledLine.FromText(text, TextStyle.Default);

    [Test]
    public async Task Gag_SuppressesLine()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger { Pattern = "spam", Actions = new TriggerActions { Gag = true } });
        var result = engine.Process(Line("this is spam here"));
        await Assert.That(result.Suppress).IsTrue();
    }

    [Test]
    public async Task NoMatch_PassesLineThrough()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger { Pattern = "nothing", Actions = new TriggerActions { Gag = true } });
        var result = engine.Process(Line("hello"));
        await Assert.That(result.Suppress).IsFalse();
        await Assert.That(result.Matched).IsEmpty();
    }

    [Test]
    public async Task Highlight_RecoloursMatchedRegion()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger
        {
            Pattern = "gold",
            Actions = new TriggerActions { HighlightForeground = TerminalColor.FromIndex(11) },
        });
        var result = engine.Process(Line("you find gold today"));

        // The span covering "gold" must carry the highlight colour.
        var goldSpan = result.Line.Spans.First(s => s.Text.Contains("gold"));
        await Assert.That(goldSpan.Style.Foreground).IsEqualTo(TerminalColor.FromIndex(11));
    }

    [Test]
    public async Task Rewrite_ReplacesLineText_WithCaptureGroups()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger
        {
            Pattern = @"(\w+) tells you: (.*)",
            Actions = new TriggerActions { Rewrite = "[PM from $1] $2" },
        });
        var result = engine.Process(Line("Bob tells you: hello there"));
        await Assert.That(result.Line.Text).IsEqualTo("[PM from Bob] hello there");
    }

    [Test]
    public async Task SendResponse_ExpandsCaptureGroups()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger
        {
            Pattern = @"^(\w+) pokes you",
            Actions = new TriggerActions { SendResponse = "poke $1" },
        });
        var result = engine.Process(Line("Alice pokes you"));
        await Assert.That(result.Responses).HasSingleItem();
        await Assert.That(result.Responses[0]).IsEqualTo("poke Alice");
    }

    [Test]
    public async Task SpawnTarget_IsCollected()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger { Pattern = "chat", Actions = new TriggerActions { SpawnTarget = "Chat" } });
        var result = engine.Process(Line("[chat] hi"));
        await Assert.That(result.SpawnTargets).Contains("Chat");
    }

    [Test]
    public async Task ScriptCallback_IsCollectedWithMatch()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger { Pattern = @"hp: (\d+)", Actions = new TriggerActions { ScriptCallback = "onHp" } });
        var result = engine.Process(Line("hp: 42"));
        await Assert.That(result.ScriptInvocations).HasSingleItem();
        await Assert.That(result.ScriptInvocations[0].Callback).IsEqualTo("onHp");
        await Assert.That(result.ScriptInvocations[0].Match.Groups[1].Value).IsEqualTo("42");
    }

    [Test]
    public async Task DisabledTrigger_IsSkipped()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger { Pattern = "x", Enabled = false, Actions = new TriggerActions { Gag = true } });
        var result = engine.Process(Line("xxx"));
        await Assert.That(result.Suppress).IsFalse();
    }

    [Test]
    public async Task StopProcessing_HaltsFurtherTriggers()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger { Pattern = "foo", StopProcessing = true, Actions = new TriggerActions { SendResponse = "first" } });
        engine.Add(new Trigger { Pattern = "foo", Actions = new TriggerActions { SendResponse = "second" } });
        var result = engine.Process(Line("foo"));
        await Assert.That(result.Responses).HasSingleItem();
        await Assert.That(result.Responses[0]).IsEqualTo("first");
    }

    [Test]
    public async Task CaseInsensitive_ByDefault()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger { Pattern = "HELLO", Actions = new TriggerActions { Gag = true } });
        var result = engine.Process(Line("hello world"));
        await Assert.That(result.Suppress).IsTrue();
    }
}
