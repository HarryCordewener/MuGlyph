using System.Text.Json;
using System.Text.Json.Nodes;
using SharpMUTerm.Core.Configuration;

namespace SharpMUTerm.Core.Tests.Configuration;

/// <summary>
/// What a launch dials, decided without a terminal. The rule replaced one that could not be expressed
/// at all: the client connected <c>config.Worlds.FirstOrDefault()</c> as its first character on every
/// run, so a user with three worlds got the first one's first character whether they wanted a
/// connection or not, and there was no way to name a different one and no way to decline.
/// </summary>
public class StartupConnectionsTests
{
    private static CharacterDefinition Character(string name, bool atStartup = false) =>
        new() { Name = name, ConnectAtStartup = atStartup };

    private static WorldDefinition World(string name, params CharacterDefinition[] characters) =>
        new() { Name = name, Host = name.ToLowerInvariant() + ".example", Port = 4201, Characters = characters.ToList() };

    private static AppConfiguration Config(params WorldDefinition[] worlds) =>
        new() { Worlds = worlds.ToList() };

    /// <summary>
    /// The default, and the headline change: a configuration nobody has marked connects nothing. Asserted
    /// on a config that has worlds <em>and</em> characters, because the interesting claim is not "an empty
    /// config connects nothing" — it is that a fully populated one does too, unless asked.
    /// </summary>
    [Test]
    public async Task NothingMarked_ConnectsNothing()
    {
        var config = Config(
            World("Aetherfall", Character("Corvid"), Character("Rookery")),
            World("Grapevine", Character("Thistle")));

        await Assert.That(StartupConnections.Resolve(config)).IsEmpty();
    }

    /// <summary>One mark, one connection — and it is the marked character, not the first one.</summary>
    [Test]
    public async Task OneMarked_ConnectsThatCharacterAndNoOther()
    {
        var config = Config(World("Aetherfall", Character("Corvid"), Character("Rookery", atStartup: true)));

        var startup = StartupConnections.Resolve(config);

        await Assert.That(startup.Count).IsEqualTo(1);
        await Assert.That(startup[0].Character!.Name).IsEqualTo("Rookery");
        await Assert.That(startup[0].World.Name).IsEqualTo("Aetherfall");
    }

    /// <summary>
    /// Several, across different worlds, in configuration order — which is the order their windows are
    /// created in and therefore what decides which one the client lands on. Ordered rather than merely
    /// counted, because "you end up somewhere predictable" is the property, and a set would satisfy the
    /// count while leaving focus to whichever server answered first.
    /// </summary>
    [Test]
    public async Task SeveralMarkedAcrossWorlds_ComeBackInConfigurationOrder()
    {
        var config = Config(
            World("Aetherfall", Character("Corvid", atStartup: true), Character("Rookery", atStartup: true)),
            World("Grapevine", Character("Thistle", atStartup: true)),
            World("Quiet", Character("Nobody")));

        var startup = StartupConnections.Resolve(config);

        await Assert.That(startup.Select(s => $"{s.World.Name}.{s.Character!.Name}").ToArray())
            .IsEquivalentTo(new[] { "Aetherfall.Corvid", "Aetherfall.Rookery", "Grapevine.Thistle" });
    }

    /// <summary>
    /// A host typed at a shell is explicit intent for <em>this</em> run and wins outright: it is dialled,
    /// and the marks are not. Anything else would have a client launched at one server quietly opening
    /// every world the user happens to have marked.
    /// </summary>
    [Test]
    public async Task AHostOnTheCommandLineWinsOverEveryMark()
    {
        var config = Config(
            World("Aetherfall", Character("Corvid", atStartup: true)),
            World("Grapevine", Character("Thistle", atStartup: true)));
        var typed = new WorldDefinition { Name = "aardmud.org", Host = "aardmud.org", Port = 4000 };

        var startup = StartupConnections.Resolve(config, typed);

        await Assert.That(startup.Count).IsEqualTo(1);
        await Assert.That(startup[0].World).IsSameReferenceAs(typed);

        // No character: a host and a port name a server, not somebody to be. The session is anonymous,
        // which is what it has always been for a command-line host.
        await Assert.That(startup[0].Character).IsNull();
    }

    /// <summary>And it wins over an empty configuration too — the command line does not need one.</summary>
    [Test]
    public async Task AHostOnTheCommandLineNeedsNoConfiguredWorld()
    {
        var typed = new WorldDefinition { Name = "aardmud.org", Host = "aardmud.org", Port = 4000 };

        var startup = StartupConnections.Resolve(new AppConfiguration(), typed);

        await Assert.That(startup.Count).IsEqualTo(1);
        await Assert.That(startup[0].World).IsSameReferenceAs(typed);
    }

    /// <summary>
    /// A world handed in whole — not built from a bare host — keeps its own first character, so the
    /// command-line arm does not quietly downgrade a real world to an anonymous session.
    /// </summary>
    [Test]
    public async Task ACommandLineWorldWithCharactersConnectsAsItsFirst()
    {
        var world = World("Aetherfall", Character("Corvid"), Character("Rookery"));

        var startup = StartupConnections.Resolve(new AppConfiguration(), world);

        await Assert.That(startup[0].Character!.Name).IsEqualTo("Corvid");
    }

    /// <summary>The mark survives a save and a load, which is the whole point of it being configuration.</summary>
    [Test]
    public async Task TheMarkRoundTripsThroughConfigJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpmuterm-startup-{Guid.NewGuid():N}", "config.json");
        try
        {
            var config = Config(World("Aetherfall", Character("Corvid", atStartup: true), Character("Rookery")));
            ConfigurationStore.Save(path, config);

            // The field is on disk under its own camelCase name, and only on the character that carries
            // it — asserted against the JSON as well as the object, because "it round-tripped" would also
            // be true of a value the serializer wrote for everybody.
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var characters = json["worlds"]!.AsArray()[0]!["characters"]!.AsArray();
            await Assert.That(characters[0]!["connectAtStartup"]!.GetValue<bool>()).IsTrue();
            await Assert.That(characters[1]!["connectAtStartup"]!.GetValue<bool>()).IsFalse();

            var reloaded = ConfigurationStore.Load(path);
            await Assert.That(reloaded.Worlds[0].Characters[0].ConnectAtStartup).IsTrue();
            await Assert.That(reloaded.Worlds[0].Characters[1].ConnectAtStartup).IsFalse();
            await Assert.That(StartupConnections.Resolve(reloaded).Count).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>
    /// A configuration written before the field existed loads as unmarked, and no migration marks
    /// anybody. That is deliberate rather than an oversight: reinstating "the first world's first
    /// character, unasked" would re-impose it on exactly the users who never chose it. The client says
    /// what it is doing instead (see <c>SharpMUTermApp.NothingAtStartNotice</c>).
    /// </summary>
    [Test]
    public async Task AConfigurationWrittenBeforeTheFieldExistedMarksNobody()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpmuterm-startup-{Guid.NewGuid():N}", "config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(JsonNode.Parse("""
                {
                  "version": 3,
                  "worlds": [
                    {
                      "name": "Aetherfall",
                      "host": "aetherfall.mux",
                      "port": 4201,
                      "characters": [ { "name": "Corvid", "autoLogin": true } ]
                    }
                  ]
                }
                """)));

            var loaded = ConfigurationStore.Load(path);

            // The character still logs itself in — v4 rewrote `autoLogin: true` into the connect line it
            // was already sending — and that did not drag a connection in with it. The two remain
            // independent facts, which is what this direction proves on an existing config; the
            // assertion moved from the removed flag onto the behaviour the flag used to name.
            await Assert.That(loaded.Worlds[0].Characters[0].Login()).IsEqualTo(LoginPlan.WithoutPassword);
            await Assert.That(loaded.Worlds[0].Characters[0].ConnectAtStartup).IsFalse();
            await Assert.That(StartupConnections.Resolve(loaded)).IsEmpty();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
