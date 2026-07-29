using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;

namespace SharpMUTerm.Core.Tests.Session;

/// <summary>
/// The one place sessions are created. What is asserted here is the seam the shell goes through
/// when it connects: a world with a character opens <em>as</em> that character, with the character's
/// trigger sets composed in and the app-wide preferences passed by reference — which is what makes
/// the F2/F3/F6 screens' edits reach a running session at all.
/// </summary>
public class SessionManagerTests
{
    private static AppConfiguration Config()
    {
        var set = new TriggerSet { Name = "Comms" };
        set.Triggers.Add(new Trigger { Name = "pages", Pattern = "pages:" });
        set.Aliases.Add(new Alias { Name = "k", Pattern = "^k$", Substitution = "kill" });
        set.Macros.Add(new Macro { Name = "survey", Key = "Num3", Command = "look" });

        var character = new CharacterDefinition { Name = "Corvid", TriggerSets = { "Comms" } };
        var world = new WorldDefinition { Name = "Aetherfall", Host = "h", Port = 1 };
        world.Characters.Add(character);

        return new AppConfiguration { Worlds = { world }, TriggerSets = { set } };
    }

    [Test]
    public async Task OpeningAsACharacter_ComposesThatCharactersAutomation()
    {
        var config = Config();
        var world = config.Worlds[0];
        var character = world.Characters[0];
        await using var manager = new SessionManager();

        var session = manager.Open(world, character, config.ResolveTriggerSets(character));

        await Assert.That(session.SessionKey).IsEqualTo("Aetherfall.Corvid");
        await Assert.That(session.Triggers.Triggers.Count).IsEqualTo(1);
        await Assert.That(session.Aliases.Aliases.Count).IsEqualTo(1);
        await Assert.That(session.Macros.Resolve("Num3")).IsNotNull();
    }

    /// <summary>
    /// The engine holds the <em>same</em> rule object the F2 screen edits, so retyping a pattern is
    /// seen by the next line rather than by the next launch. Adding or removing a rule is not — the
    /// engine was handed the list once — which is the distinction the screens have to live with.
    /// </summary>
    [Test]
    public async Task AnOpenSession_SeesLaterEditsToTheRulesItWasGiven()
    {
        var config = Config();
        var world = config.Worlds[0];
        var character = world.Characters[0];
        await using var manager = new SessionManager();
        var session = manager.Open(world, character, config.ResolveTriggerSets(character));

        config.TriggerSets[0].Triggers[0].Pattern = "whispers:";

        await Assert.That(session.Triggers.Triggers[0].Regex.IsMatch("Anvil whispers: hello")).IsTrue();
        await Assert.That(session.Triggers.Triggers[0].Regex.IsMatch("Anvil pages: hello")).IsFalse();
    }

    [Test]
    public async Task OpeningAnonymously_KeysOnTheWorldAndCarriesNoAutomation()
    {
        var config = Config();
        await using var manager = new SessionManager();

        var session = manager.Open(config.Worlds[0], config.ScrollbackLines, config.Text, config.Input);

        await Assert.That(session.SessionKey).IsEqualTo("Aetherfall");
        await Assert.That(session.Character).IsNull();
        await Assert.That(session.Triggers.Triggers).IsEmpty();
    }

    [Test]
    public async Task OpenedSessions_AreFindableByTheirKey()
    {
        var config = Config();
        var world = config.Worlds[0];
        await using var manager = new SessionManager();

        manager.Open(world, world.Characters[0], config.ResolveTriggerSets(world.Characters[0]));

        await Assert.That(manager.Find("Aetherfall.Corvid")).IsNotNull();
        await Assert.That(manager.Find("Aetherfall")).IsNull();
    }
}
