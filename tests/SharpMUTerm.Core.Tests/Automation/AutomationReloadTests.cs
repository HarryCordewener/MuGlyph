using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Tests.Session;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Automation;

/// <summary>
/// Editing automation while a session is connected. The engines used to be handed a flat list of rules at
/// construction, so <em>membership</em> was frozen for the life of a session even though every property of
/// an individual rule was already live (<see cref="Trigger.Pattern"/> drops its compiled regex on write,
/// <see cref="MacroEngine"/> re-reads <see cref="Macro.Key"/> on every lookup). Assigning a trigger set to
/// a character, or adding a rule to a set the character already had, therefore did nothing at all until the
/// session was reconnected — and nothing anywhere said so.
/// </summary>
public class AutomationReloadTests
{
    // ---- the engines ------------------------------------------------------------------------

    /// <summary>
    /// The mechanism: configured rules are swappable, runtime ones are not touched. The split exists
    /// because the scripting layer adds triggers through <see cref="TriggerEngine.Add"/>, and a reload that
    /// simply refilled one list would delete a Lua rule every time somebody ticked a checkbox — the reload
    /// runs after every committed settings change.
    /// </summary>
    [Test]
    public async Task ReplaceConfiguredSwapsTheConfiguredRulesAndKeepsTheRuntimeOnes()
    {
        var fromScript = new Trigger { Name = "lua", Pattern = "lua" };
        var engine = new TriggerEngine(new[] { new Trigger { Name = "old", Pattern = "old" } });
        engine.Add(fromScript);

        engine.ReplaceConfigured(new[] { new Trigger { Name = "new", Pattern = "new" } });

        await Assert.That(engine.Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "new", "lua" });
    }

    /// <summary>Configured rules keep evaluating first, so the F2 screen's order stays the engine's order.</summary>
    [Test]
    public async Task ConfiguredRulesAreEvaluatedBeforeRuntimeOnes()
    {
        var engine = new TriggerEngine();
        engine.Add(new Trigger { Name = "runtime", Pattern = "hello", StopProcessing = true });
        engine.ReplaceConfigured(new[] { new Trigger { Name = "configured", Pattern = "hello", StopProcessing = true } });

        var result = engine.Process(StyledLine.FromText("hello", TextStyle.Default));

        await Assert.That(result.Matched.Select(t => t.Name)).IsEquivalentTo(new[] { "configured" });
    }

    /// <summary>
    /// A rule's match count is its own history, so it survives a reload that keeps the rule. Losing it
    /// whenever an unrelated setting was committed would make "this capture has never matched" — the one
    /// fact that answers "why did no window open?" — unreadable.
    /// </summary>
    [Test]
    public async Task MatchCountsSurviveAReloadForTheRulesThatSurviveIt()
    {
        var kept = new Trigger { Name = "kept", Pattern = "chat" };
        var engine = new TriggerEngine(new[] { kept });
        engine.Process(StyledLine.FromText("chat: hello", TextStyle.Default));
        await Assert.That(engine.MatchesFor(kept)).IsEqualTo(1);

        engine.ReplaceConfigured(new[] { kept, new Trigger { Name = "added", Pattern = "page" } });

        await Assert.That(engine.MatchesFor(kept)).IsEqualTo(1);
        await Assert.That(engine.LinesProcessed).IsEqualTo(1);
    }

    /// <summary>A rule this engine never ran has no matches, which is the state a broken capture is in.</summary>
    [Test]
    public async Task ACaptureThatHasNeverMatchedCountsZero()
    {
        var never = new Trigger { Name = "public", Pattern = "^<Public>" };
        var engine = new TriggerEngine(new[] { never });

        engine.Process(StyledLine.FromText("Nothing like it", TextStyle.Default));

        await Assert.That(engine.MatchesFor(never)).IsEqualTo(0);
        await Assert.That(engine.LinesProcessed).IsEqualTo(1);
    }

    /// <summary>Aliases and bindings are the same defect and get the same treatment.</summary>
    [Test]
    public async Task AliasesAndMacrosReloadTheSameWay()
    {
        var aliases = new AliasEngine();
        aliases.ReplaceConfigured(new[] { new Alias { Name = "k", Pattern = "^k$", Substitution = "kill" } });
        await Assert.That(aliases.Expand("k").Commands).IsEquivalentTo(new[] { "kill" });

        var macros = new MacroEngine();
        macros.ReplaceConfigured(new[] { new Macro { Name = "look", Key = "F1", Command = "look" } });
        await Assert.That(macros.Resolve("F1")!.Command).IsEqualTo("look");
    }

    /// <summary>
    /// A runtime binding still wins its key, which is what <see cref="MacroEngine.Add"/>'s "replaces
    /// whichever one holds it" meant before the configured list became reloadable.
    /// </summary>
    [Test]
    public async Task ARuntimeBindingShadowsTheConfiguredOneOnItsKey()
    {
        var macros = new MacroEngine(new[] { new Macro { Name = "cfg", Key = "F1", Command = "look" } });
        macros.Add(new Macro { Name = "script", Key = "f1", Command = "north" });

        await Assert.That(macros.Resolve("F1")!.Command).IsEqualTo("north");
    }

    // ---- the session -----------------------------------------------------------------------

    /// <summary>
    /// The session-level claim, on a <strong>connected</strong> session: a trigger added to an assigned set
    /// while the connection is live routes the next matching line to its spawn window. Before the fix the
    /// engine still held the empty list it was built with and <see cref="WorldSession.SpawnLine"/> never
    /// fired at all.
    /// </summary>
    [Test]
    public async Task ATriggerAddedToALiveSessionsSetRoutesTheNextLine()
    {
        var set = new TriggerSet { Name = "Comms" };
        var telnet = new FakeTelnetSession();
        var session = new WorldSession(
            new WorldDefinition { Name = "Convergence", Host = "x", Port = 4201 },
            new CharacterDefinition { Name = "Mannaz", TriggerSets = { "Comms" } },
            new[] { set },
            _ => telnet);

        var spawned = new List<string>();
        session.SpawnLine += (_, e) => spawned.Add($"{e.Target}|{e.Line.Text}");
        await session.ConnectAsync();

        telnet.EmitLine("<Public> Lucille says, \"Lol\"");
        await Assert.That(spawned).IsEmpty(); // nothing is configured yet

        // What F2's [+ add trigger] does to the set the character is already assigned…
        set.Triggers.Add(new Trigger
        {
            Name = "Public",
            Pattern = "^<Public>",
            Actions = new TriggerActions { SpawnTarget = "Public" },
        });

        // …and what the app does after every committed settings change.
        session.ReloadAutomation(new[] { set });

        telnet.EmitLine("<Public> Lucille says, \"Lol\"");

        await Assert.That(spawned).IsEquivalentTo(new[] { "Public|<Public> Lucille says, \"Lol\"" });
    }

    /// <summary>
    /// And the other half of the reported case: the <em>set</em> was not assigned to the character when the
    /// session opened. Resolution is the app's (it owns the configuration), so the session is simply handed
    /// the sets that resolve now — which is exactly what a mid-evening F5 assignment changes.
    /// </summary>
    [Test]
    public async Task ASetAssignedAfterTheSessionOpenedBecomesLiveWhenItIsReloaded()
    {
        var config = new AppConfiguration();
        var set = new TriggerSet
        {
            Name = "Comms",
            Triggers =
            {
                new Trigger
                {
                    Name = "Public",
                    Pattern = "^<Public>",
                    Actions = new TriggerActions { SpawnTarget = "Public" },
                },
            },
        };
        config.TriggerSets.Add(set);

        var character = new CharacterDefinition { Name = "Mannaz" };
        var session = new WorldSession(
            new WorldDefinition { Name = "Convergence", Host = "x", Port = 4201 },
            character,
            config.ResolveTriggerSets(character));

        await Assert.That(session.Triggers.Triggers).IsEmpty();

        character.TriggerSets.Add("Comms"); // WorldsScreenRenderer.Assignment, mid-connection
        session.ReloadAutomation(config.ResolveTriggerSets(character));

        await Assert.That(session.Triggers.Triggers.Select(t => t.Name)).IsEquivalentTo(new[] { "Public" });
    }
}
