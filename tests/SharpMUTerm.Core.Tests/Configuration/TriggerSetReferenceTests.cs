using SharpMUTerm.Core.Configuration;

namespace SharpMUTerm.Core.Tests.Configuration;

/// <summary>
/// The links between a trigger set and the characters that opted into it. A character selects its
/// automation by <em>name</em>, so a set that is renamed or deleted leaves every one of those
/// references dangling unless something goes and fixes them — and "fixes them" has to mean putting each
/// one back in the position it held, because that order decides which set wins a conflict
/// (<see cref="AppConfiguration.ResolveTriggerSets"/>).
/// </summary>
public class TriggerSetReferenceTests
{
    private static AppConfiguration Config()
    {
        var config = new AppConfiguration();
        config.TriggerSets.Add(new TriggerSet { Name = "Comms" });
        config.TriggerSets.Add(new TriggerSet { Name = "Trade" });
        config.Worlds.Add(new WorldDefinition
        {
            Name = "Aetherfall",
            Characters =
            {
                new CharacterDefinition { Name = "Corvid", TriggerSets = { "Combat", "Comms", "Trade" } },
                new CharacterDefinition { Name = "Rookery", TriggerSets = { "Trade" } },
            },
        });
        config.Worlds.Add(new WorldDefinition
        {
            Name = "Grapevine",
            Characters = { new CharacterDefinition { Name = "Thistle", TriggerSets = { "Comms" } } },
        });

        return config;
    }

    [Test]
    public async Task Find_ReportsEveryCharacterThatOptedIn_AndWhereInItsOwnList()
    {
        var config = Config();

        var found = TriggerSetReferences.Find(config.Worlds, "Comms");

        await Assert.That(found).Count().IsEqualTo(2);
        await Assert.That(found[0].Character.Name).IsEqualTo("Corvid");
        await Assert.That(found[0].Index).IsEqualTo(1);
        await Assert.That(found[1].Character.Name).IsEqualTo("Thistle");
        await Assert.That(found[1].Index).IsEqualTo(0);
    }

    /// <summary>
    /// Matching follows the resolver, which is case-insensitive. A reference that <em>would resolve</em>
    /// to this set is a reference to it, whatever case it was typed in — otherwise renaming the set
    /// would silently leave that character behind.
    /// </summary>
    [Test]
    public async Task Find_MatchesTheWayTheResolverDoes()
    {
        var config = Config();
        config.Worlds[0].Characters[1].TriggerSets[0] = "trade";

        await Assert.That(TriggerSetReferences.Find(config.Worlds, "Trade")).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Find_ComesBackEmptyForASetNobodyUses()
    {
        await Assert.That(TriggerSetReferences.Find(Config().Worlds, "Nobody")).IsEmpty();
    }

    /// <summary>
    /// The point of renaming through the references rather than around them: the assignment keeps its
    /// place in the character's list, so the priority order somebody built by hand survives a typo being
    /// fixed. Renaming in place is also what makes undo free — the same call with the old name.
    /// </summary>
    [Test]
    public async Task Rename_RewritesEveryReferenceWithoutMovingIt()
    {
        var config = Config();
        var corvid = config.Worlds[0].Characters[0];

        TriggerSetReferences.Rename(TriggerSetReferences.Find(config.Worlds, "Comms"), "Channels");

        await Assert.That(corvid.TriggerSets).IsEquivalentTo(new[] { "Combat", "Channels", "Trade" });
        await Assert.That(config.Worlds[1].Characters[0].TriggerSets).IsEquivalentTo(new[] { "Channels" });

        // Nothing else moved: the set nobody renamed is where it was, in the position it was in.
        await Assert.That(config.Worlds[0].Characters[1].TriggerSets).IsEquivalentTo(new[] { "Trade" });
    }

    /// <summary>
    /// Deleting a set has to take the assignments with it, or a character is left holding a name that
    /// resolves to nothing — and the screen would go on drawing it as an assignment.
    /// </summary>
    [Test]
    public async Task Detach_RemovesEveryReference()
    {
        var config = Config();

        TriggerSetReferences.Detach(TriggerSetReferences.Find(config.Worlds, "Trade"));

        await Assert.That(config.Worlds[0].Characters[0].TriggerSets)
            .IsEquivalentTo(new[] { "Combat", "Comms" });
        await Assert.That(config.Worlds[0].Characters[1].TriggerSets).IsEmpty();
        await Assert.That(config.Worlds[1].Characters[0].TriggerSets).IsEquivalentTo(new[] { "Comms" });
    }

    /// <summary>
    /// Undoing a deletion restores the <em>position</em>, not merely the existence, of every assignment
    /// — the same rule a deleted row's undo follows. Put back on the end, an assignment would come back
    /// at a different priority than it left at, which is a second edit riding along with the first.
    /// </summary>
    [Test]
    public async Task DetachThenReattach_PutsEveryReferenceBackWhereItWas()
    {
        var config = Config();
        var corvid = config.Worlds[0].Characters[0];
        var references = TriggerSetReferences.Find(config.Worlds, "Comms");

        TriggerSetReferences.Detach(references);
        await Assert.That(corvid.TriggerSets).IsEquivalentTo(new[] { "Combat", "Trade" });

        TriggerSetReferences.Reattach(references, "Comms");

        await Assert.That(corvid.TriggerSets[0]).IsEqualTo("Combat");
        await Assert.That(corvid.TriggerSets[1]).IsEqualTo("Comms");
        await Assert.That(corvid.TriggerSets[2]).IsEqualTo("Trade");
        await Assert.That(config.Worlds[1].Characters[0].TriggerSets).IsEquivalentTo(new[] { "Comms" });
    }

    /// <summary>
    /// A character holding several references to one set is the case the walk order exists for: removing
    /// the earlier one renumbers the later, so <c>Detach</c> walks backwards and <c>Reattach</c> forwards.
    /// Both survive it.
    /// </summary>
    [Test]
    public async Task DetachThenReattach_SurvivesTwoReferencesInOneCharacter()
    {
        var config = Config();
        var corvid = config.Worlds[0].Characters[0];
        corvid.TriggerSets.Add("Comms");
        var references = TriggerSetReferences.Find(config.Worlds, "Comms");

        TriggerSetReferences.Detach(references);
        await Assert.That(corvid.TriggerSets).IsEquivalentTo(new[] { "Combat", "Trade" });

        TriggerSetReferences.Reattach(references, "Comms");

        await Assert.That(corvid.TriggerSets).IsEquivalentTo(new[] { "Combat", "Comms", "Trade", "Comms" });
    }

    /// <summary>
    /// The round trip that matters at the top: after a rename, the character still resolves to the very
    /// same set object. Nothing else on these screens can break automation by editing a label.
    /// </summary>
    [Test]
    public async Task Rename_KeepsTheCharacterResolvingToTheSameSet()
    {
        var config = Config();
        var corvid = config.Worlds[0].Characters[0];
        var comms = config.TriggerSets[0];

        TriggerSetReferences.Rename(TriggerSetReferences.Find(config.Worlds, comms.Name), "Channels");
        comms.Name = "Channels";

        await Assert.That(config.ResolveTriggerSets(corvid)).Contains(comms);
    }
}
