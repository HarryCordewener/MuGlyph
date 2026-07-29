using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Automation;

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
        // ...and the whole line carries a left-rule colour so the UI can mark it.
        await Assert.That(result.Line.RuleColor).IsEqualTo(TerminalColor.FromIndex(11));
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

    /// <summary>
    /// The pattern is settable so the F2 settings screen can edit a live trigger. The compiled regex
    /// is cached, so writing it has to drop that cache — otherwise the rule keeps matching the pattern
    /// it no longer has, which is invisible until a line arrives.
    /// </summary>
    [Test]
    public async Task RewritingThePattern_RecompilesTheMatcher()
    {
        var trigger = new Trigger { Pattern = "spam", Actions = new TriggerActions { Gag = true } };
        var engine = new TriggerEngine();
        engine.Add(trigger);
        await Assert.That(engine.Process(Line("this is spam")).Suppress).IsTrue();

        trigger.Pattern = "noise";

        await Assert.That(engine.Process(Line("this is spam")).Suppress).IsFalse();
        await Assert.That(engine.Process(Line("this is noise")).Suppress).IsTrue();
    }

    /// <summary>
    /// Case sensitivity is settable so the F2 settings screen can flip it — F3 has always offered it on
    /// an alias, and the asymmetry was arbitrary. It is the second property on a trigger with cached
    /// derived state: the casing is compiled into <see cref="Trigger.Regex"/>'s options, so writing it
    /// has to drop that cache or the rule goes on matching with the old options, invisibly, until a line
    /// arrives.
    /// </summary>
    [Test]
    public async Task FlippingCaseSensitivity_RecompilesTheMatcher()
    {
        var trigger = new Trigger { Pattern = "HELLO", Actions = new TriggerActions { Gag = true } };
        var engine = new TriggerEngine();
        engine.Add(trigger);
        await Assert.That(engine.Process(Line("hello world")).Suppress).IsTrue();

        trigger.CaseSensitive = true;

        await Assert.That(engine.Process(Line("hello world")).Suppress).IsFalse();
        await Assert.That(engine.Process(Line("HELLO world")).Suppress).IsTrue();

        // And back, so the cache is dropped in both directions rather than only on the way up.
        trigger.CaseSensitive = false;
        await Assert.That(engine.Process(Line("hello world")).Suppress).IsTrue();
    }

    /// <summary>
    /// The four action values the F2 screen now edits are settable, and — unlike the casing — carry no
    /// cache at all: <see cref="TriggerEngine"/> reads each one per match, so an edit applies to the very
    /// next line. Asserted together, because "check for cached derived state" is a question that has to
    /// be answered for every property that becomes writable, and this is the answer for these four.
    /// </summary>
    [Test]
    public async Task EditingTheActions_AppliesToTheNextLineWithNothingCached()
    {
        var trigger = new Trigger { Pattern = @"hp: (\d+)", Actions = new TriggerActions() };
        var engine = new TriggerEngine();
        engine.Add(trigger);

        var before = engine.Process(Line("hp: 42"));
        await Assert.That(before.Responses).IsEmpty();
        await Assert.That(before.ScriptInvocations).IsEmpty();
        await Assert.That(before.Line.Text).IsEqualTo("hp: 42");

        trigger.Actions.Rewrite = "low: $1";
        trigger.Actions.SendResponse = "quaff potion";
        trigger.Actions.ScriptCallback = "onHp";
        trigger.Actions.AddAttributes = TextAttributes.Bold;

        var after = engine.Process(Line("hp: 42"));
        await Assert.That(after.Line.Text).IsEqualTo("low: 42");
        await Assert.That(after.Responses).HasSingleItem();
        await Assert.That(after.Responses[0]).IsEqualTo("quaff potion");
        await Assert.That(after.ScriptInvocations).HasSingleItem();
        await Assert.That(after.ScriptInvocations[0].Callback).IsEqualTo("onHp");

        // Clearing them turns each action back off, again with no stale copy anywhere.
        trigger.Actions.Rewrite = null;
        trigger.Actions.SendResponse = null;
        trigger.Actions.ScriptCallback = null;

        var cleared = engine.Process(Line("hp: 42"));
        await Assert.That(cleared.Line.Text).IsEqualTo("hp: 42");
        await Assert.That(cleared.Responses).IsEmpty();
        await Assert.That(cleared.ScriptInvocations).IsEmpty();
    }
}
