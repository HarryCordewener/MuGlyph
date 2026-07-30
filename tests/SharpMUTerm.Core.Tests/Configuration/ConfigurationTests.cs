using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Configuration;

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
    public async Task RoundTrip_PreservesEncodingKeepaliveAndTimers()
    {
        var config = new AppConfiguration
        {
            Worlds =
            {
                new WorldDefinition { Name = "W", Encoding = "ISO-8859-1", KeepaliveSeconds = 45 },
            },
            TriggerSets =
            {
                new TriggerSet
                {
                    Name = "Ticks",
                    Timers = { new TimerDefinition { Name = "heartbeat", IntervalSeconds = 30, Command = "look" } },
                },
            },
        };

        var restored = ConfigurationStore.Deserialize(ConfigurationStore.Serialize(config));

        await Assert.That(restored.Worlds[0].Encoding).IsEqualTo("ISO-8859-1");
        await Assert.That(restored.Worlds[0].KeepaliveSeconds).IsEqualTo(45);
        var timer = restored.TriggerSets[0].Timers[0];
        await Assert.That(timer.Name).IsEqualTo("heartbeat");
        await Assert.That(timer.IntervalSeconds).IsEqualTo(30d);
        await Assert.That(timer.Command).IsEqualTo("look");
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

    /// <summary>
    /// The configuration document carries no password. <b>This assertion has inverted twice in one day and is
    /// back where it started, for a completely different reason</b>, which is worth spelling out so nobody
    /// "restores" the wrong one.
    /// <para>
    /// It began as <c>Password_IsNeverSerialized</c>, pinning the absence of the secret because
    /// <see cref="CharacterDefinition.Password"/> was <c>[JsonIgnore]</c> session state — the client forgot
    /// your password, and this test was the proof. It briefly became <c>Password_IsSerializedAndComesBack</c>
    /// when the decision was to save passwords into <c>config.json</c> in plaintext. It is now the absence
    /// again, but the client <em>does</em> save your password: it goes into
    /// <see cref="SecretsStore"/>'s own file, and this document holds only a
    /// <see cref="CharacterDefinition.PasswordRef"/> GUID.
    /// </para>
    /// <para>
    /// So this is the strongest of the three claims rather than a retreat to the weakest. The old version
    /// held because nothing was persisted; this one holds while the password is persisted and reloadable,
    /// which is the property that makes a pasted config safe. The persistence half lives in
    /// <see cref="PasswordAtRestTests"/>, and the two must be read together — either alone describes a design
    /// this project has already rejected.
    /// </para>
    /// </summary>
    [Test]
    public async Task Password_IsNotWrittenIntoTheConfigDocument()
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
    public async Task LastSession_RoundTripsThroughTheStore()
    {
        var ws = new Workspace(mainWindowId: "main", mainTitle: "main", sessionKey: "Aetherfall.Corvid");
        var chat = ws.RouteSpawn("Chat", "Aetherfall.Corvid");
        chat.OwnerLabel = "Corvid";
        var config = new AppConfiguration { LastSession = WorkspaceState.Capture(ws) };

        var restored = ConfigurationStore.Deserialize(ConfigurationStore.Serialize(config));

        await Assert.That(restored.LastSession).IsNotNull();
        var workspace = restored.LastSession!.Restore();
        await Assert.That(workspace.FindWindow(Workspace.SpawnWindowId("Chat"))!.OwnerLabel).IsEqualTo("Corvid");
    }

    [Test]
    public async Task TextAndInputPreferences_RoundTripThroughTheStore()
    {
        var config = new AppConfiguration();
        config.Text.StripIncomingColour = true;
        config.Text.UnderlineHyperlinks = false;
        config.Text.EmojiSubstitution = false;
        config.Input.LocalEcho = false;
        config.Input.KeepDrafts = false;

        var restored = ConfigurationStore.Deserialize(ConfigurationStore.Serialize(config));

        await Assert.That(restored.Text.StripIncomingColour).IsTrue();
        await Assert.That(restored.Text.AllowBlink).IsFalse();
        await Assert.That(restored.Text.UnderlineHyperlinks).IsFalse();
        await Assert.That(restored.Text.EmojiSubstitution).IsFalse();
        await Assert.That(restored.Input.LocalEcho).IsFalse();
        await Assert.That(restored.Input.KeepDrafts).IsFalse();
    }

    [Test]
    public async Task TextAndInputPreferences_DefaultWhenAConfigPredatesThem()
    {
        // Purely additive schema: an older file simply has no "text"/"input" object.
        var restored = ConfigurationStore.Deserialize("""{"version":2,"worlds":[],"triggerSets":[]}""");

        await Assert.That(restored.Text.UnderlineHyperlinks).IsTrue();
        await Assert.That(restored.Text.EmojiSubstitution).IsTrue();
        await Assert.That(restored.Input.LocalEcho).IsTrue();
        await Assert.That(restored.Input.KeepDrafts).IsTrue();
    }

    /// <summary>
    /// A config written by a build that still had <c>ambiguousWidth</c>, <c>newlineKey</c>,
    /// <c>checkSpelling</c> and <c>dictionary</c> loads fine, keeping everything around them. Those
    /// four settings were removed along with their controls (nothing read them — there is no speller,
    /// no multi-line input, and column widths are the framework's), and a saved file naming them must
    /// not become a config the client refuses to start on.
    /// </summary>
    [Test]
    public async Task RetiredPreferenceKeys_AreIgnoredRatherThanFatal()
    {
        var restored = ConfigurationStore.Deserialize(
            """
            {"version":2,"worlds":[],"triggerSets":[],
             "text":{"stripIncomingColour":true,"ambiguousWidth":"wide"},
             "input":{"localEcho":false,"newlineKey":"Ctrl+J","checkSpelling":true,"dictionary":"en_GB"}}
            """);

        await Assert.That(restored.Text.StripIncomingColour).IsTrue();
        await Assert.That(restored.Input.LocalEcho).IsFalse();
        await Assert.That(restored.Input.KeepDrafts).IsTrue();
    }

    [Test]
    public async Task AliasCaseSensitivity_IsSettableAndDropsTheCachedRegex()
    {
        var alias = new Alias { Name = "k", Pattern = "^k$", Substitution = "kill" };
        await Assert.That(alias.Regex.IsMatch("K")).IsTrue();

        alias.CaseSensitive = true;

        await Assert.That(alias.Regex.IsMatch("K")).IsFalse();
        await Assert.That(alias.Regex.IsMatch("k")).IsTrue();
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
    /// <summary>
    /// The scrollback default, and the one definition behind it. Ten thousand rather than the twenty
    /// thousand it was: the TUI hands a window's whole buffer to one markup control whose parse cache is
    /// keyed on a content version, so a single arriving line re-parses the lot — 88-116 ms at twenty
    /// thousand lines, which is a client that stutters visibly in a busy room. Pinned here because the
    /// number is a performance decision and a silent drift back would undo it.
    /// </summary>
    [Test]
    public async Task ScrollbackDefault_IsTenThousand_AndSharedWithTheBuffer()
    {
        // Pinned through the two runtime reads rather than against the constant itself: the constant
        // has to stay a `const` (it is a default parameter value in four places), so comparing it to
        // a literal is a compile-time tautology the analyzer rightly rejects (TUnitAssertions0005).
        // Asserting the config default and the buffer's own capacity against 10_000 pins the number
        // where it is actually observable, and catches a drift the constant-to-literal form would
        // have caught anyway — plus one it would not: a consumer wired to a different default.
        await Assert.That(new AppConfiguration().ScrollbackLines).IsEqualTo(10_000);
        await Assert.That(new SharpMUTerm.Core.Text.ScrollbackBuffer().Capacity).IsEqualTo(10_000);
        await Assert.That(new AppConfiguration().ScrollbackLines)
            .IsEqualTo(SharpMUTerm.Core.Text.ScrollbackBuffer.DefaultCapacity);
    }
}
