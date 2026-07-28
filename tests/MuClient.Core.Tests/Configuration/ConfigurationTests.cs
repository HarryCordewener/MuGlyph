using MuClient.Core.Automation;
using MuClient.Core.Configuration;
using MuClient.Core.Text;

namespace MuClient.Core.Tests.Configuration;

public class ConfigurationTests
{
    [Test]
    public async Task RoundTrip_PreservesWorldsCharactersTriggerSetsAndColors()
    {
        var config = new AppConfiguration
        {
            ScrollbackLines = 5000,
            Worlds =
            {
                new WorldDefinition
                {
                    Name = "Test MUSH",
                    Host = "mush.example.org",
                    Port = 4201,
                    UseTls = true,
                    Characters =
                    {
                        new CharacterDefinition { Name = "Wizard", TriggerSets = { "Combat" } },
                    },
                },
            },
            TriggerSets =
            {
                new TriggerSet
                {
                    Name = "Combat",
                    Triggers =
                    {
                        new Trigger
                        {
                            Name = "gold",
                            Pattern = "gold",
                            Actions = new TriggerActions { HighlightForeground = TerminalColor.FromRgb(255, 215, 0) },
                        },
                    },
                    Aliases = { new Alias { Pattern = "^gt (.+)", Substitution = "\"$1" } },
                    Macros = { new Macro { Key = "F1", Command = "look" } },
                },
            },
        };

        var json = ConfigurationStore.Serialize(config);
        var restored = ConfigurationStore.Deserialize(json);

        await Assert.That(restored.Version).IsEqualTo(AppConfiguration.CurrentVersion);
        await Assert.That(restored.ScrollbackLines).IsEqualTo(5000);
        await Assert.That(restored.Worlds).HasSingleItem();
        var world = restored.Worlds[0];
        await Assert.That(world.Name).IsEqualTo("Test MUSH");
        await Assert.That(world.Port).IsEqualTo(4201);
        await Assert.That(world.UseTls).IsTrue();
        await Assert.That(world.Characters[0].Name).IsEqualTo("Wizard");
        await Assert.That(world.Characters[0].TriggerSets).Contains("Combat");

        await Assert.That(restored.TriggerSets).HasSingleItem();
        var set = restored.TriggerSets[0];
        await Assert.That(set.Triggers[0].Actions.HighlightForeground).IsEqualTo(TerminalColor.FromRgb(255, 215, 0));
        await Assert.That(set.Aliases[0].Substitution).IsEqualTo("\"$1");
        await Assert.That(set.Macros[0].Command).IsEqualTo("look");
    }

    [Test]
    public async Task ResolveTriggerSets_ReturnsCharactersSetsInOrder_SkippingMissingAndDuplicates()
    {
        var config = new AppConfiguration
        {
            TriggerSets =
            {
                new TriggerSet { Name = "Comms" },
                new TriggerSet { Name = "Trade" },
            },
        };
        var character = new CharacterDefinition
        {
            TriggerSets = { "trade", "Comms", "Missing", "Trade" },
        };

        var resolved = config.ResolveTriggerSets(character);

        await Assert.That(resolved.Select(s => s.Name)).IsEquivalentTo(new[] { "Trade", "Comms" });
    }

    [Test]
    public async Task Deserialize_EmptyDefaultsGracefully()
    {
        var config = ConfigurationStore.Deserialize("{}");
        await Assert.That(config.Worlds).IsEmpty();
        await Assert.That(config.Version).IsEqualTo(AppConfiguration.CurrentVersion);
    }

    [Test]
    public async Task Deserialize_V1Config_MigratesAutomationIntoTriggerSetsAndCharacter()
    {
        const string v1 = """
            {
              "version": 1,
              "worlds": [
                {
                  "name": "Old World",
                  "host": "old.example.net",
                  "port": 6250,
                  "triggers": [ { "name": "hail", "pattern": "waves" } ],
                  "aliases": [ { "pattern": "^gt (.+)", "substitution": "grouptell $1" } ],
                  "macros": [ { "key": "F1", "command": "look" } ],
                  "logging": { "format": "Html" }
                }
              ]
            }
            """;

        var config = ConfigurationStore.Deserialize(v1);

        await Assert.That(config.Version).IsEqualTo(AppConfiguration.CurrentVersion);
        await Assert.That(config.Worlds).HasSingleItem();
        var world = config.Worlds[0];
        await Assert.That(world.Host).IsEqualTo("old.example.net");
        await Assert.That(world.Characters).HasSingleItem();

        var character = world.Characters[0];
        await Assert.That(character.TriggerSets).Contains("Old World");
        await Assert.That(character.Logging.Format).IsEqualTo(LogFormat.Html);

        await Assert.That(config.TriggerSets).HasSingleItem();
        var set = config.TriggerSets[0];
        await Assert.That(set.Name).IsEqualTo("Old World");
        await Assert.That(set.Triggers[0].Pattern).IsEqualTo("waves");
        await Assert.That(set.Aliases[0].Substitution).IsEqualTo("grouptell $1");
        await Assert.That(set.Macros[0].Command).IsEqualTo("look");
    }

    [Test]
    public async Task Deserialize_V1Config_WithExistingCharacters_WiresMigratedSetIntoEach()
    {
        // Partially-migrated file: characters already present *and* legacy automation still on the
        // world. The lifted set must be referenced by every existing character, not orphaned.
        const string v1 = """
            {
              "version": 1,
              "worlds": [
                {
                  "name": "Hybrid",
                  "host": "h",
                  "port": 1,
                  "characters": [
                    { "name": "Alice", "triggerSets": ["Existing"] },
                    { "name": "Bob" }
                  ],
                  "triggers": [ { "pattern": "waves" } ]
                }
              ]
            }
            """;

        var config = ConfigurationStore.Deserialize(v1);

        await Assert.That(config.TriggerSets).HasSingleItem();
        var setName = config.TriggerSets[0].Name;
        await Assert.That(setName).IsEqualTo("Hybrid");

        var world = config.Worlds[0];
        await Assert.That(world.Characters).Count().IsEqualTo(2);
        await Assert.That(world.Characters[0].Name).IsEqualTo("Alice");
        await Assert.That(world.Characters[0].TriggerSets).IsEquivalentTo(new[] { "Existing", "Hybrid" });
        await Assert.That(world.Characters[1].TriggerSets).IsEquivalentTo(new[] { "Hybrid" });
    }

    [Test]
    public async Task Deserialize_V1Config_WithExistingCharacters_NoAutomation_LeavesThemUntouched()
    {
        const string v1 = """
            {
              "version": 1,
              "worlds": [
                {
                  "name": "Clean",
                  "host": "h",
                  "port": 1,
                  "characters": [ { "name": "Alice", "triggerSets": ["Existing"] } ]
                }
              ]
            }
            """;

        var config = ConfigurationStore.Deserialize(v1);

        await Assert.That(config.TriggerSets).IsEmpty();
        await Assert.That(config.Worlds[0].Characters[0].TriggerSets).IsEquivalentTo(new[] { "Existing" });
    }

    [Test]
    public async Task Password_IsNeverSerialized()
    {
        var config = new AppConfiguration
        {
            Worlds =
            {
                new WorldDefinition
                {
                    Name = "W",
                    Characters = { new CharacterDefinition { Name = "Secret", Password = "swordfish" } },
                },
            },
        };

        var json = ConfigurationStore.Serialize(config);

        await Assert.That(json).DoesNotContain("swordfish");
        await Assert.That(ConfigurationStore.Deserialize(json).Worlds[0].Characters[0].Password).IsNull();
    }

    [Test]
    public async Task ColorConverter_RoundTripsAllKinds()
    {
        await Assert.That(TerminalColorJsonConverter.ToString(TerminalColor.Default)).IsEqualTo("default");
        await Assert.That(TerminalColorJsonConverter.ToString(TerminalColor.FromIndex(196))).IsEqualTo("idx:196");
        await Assert.That(TerminalColorJsonConverter.ToString(TerminalColor.FromRgb(1, 2, 3))).IsEqualTo("rgb:1,2,3");
        await Assert.That(TerminalColorJsonConverter.Parse("idx:196")).IsEqualTo(TerminalColor.FromIndex(196));
        await Assert.That(TerminalColorJsonConverter.Parse("rgb:1,2,3")).IsEqualTo(TerminalColor.FromRgb(1, 2, 3));
        await Assert.That(TerminalColorJsonConverter.Parse("garbage")).IsEqualTo(TerminalColor.Default);
    }
}
