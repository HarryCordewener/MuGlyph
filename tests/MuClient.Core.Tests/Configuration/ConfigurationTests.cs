using MuClient.Core.Automation;
using MuClient.Core.Configuration;
using MuClient.Core.Text;

namespace MuClient.Core.Tests.Configuration;

public class ConfigurationTests
{
    [Test]
    public async Task RoundTrip_PreservesWorldsTriggersAndColors()
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

        await Assert.That(restored.ScrollbackLines).IsEqualTo(5000);
        await Assert.That(restored.Worlds).HasSingleItem();
        var world = restored.Worlds[0];
        await Assert.That(world.Name).IsEqualTo("Test MUSH");
        await Assert.That(world.Port).IsEqualTo(4201);
        await Assert.That(world.UseTls).IsTrue();
        await Assert.That(world.Triggers).HasSingleItem();
        await Assert.That(world.Triggers[0].Actions.HighlightForeground).IsEqualTo(TerminalColor.FromRgb(255, 215, 0));
        await Assert.That(world.Aliases[0].Substitution).IsEqualTo("\"$1");
        await Assert.That(world.Macros[0].Command).IsEqualTo("look");
    }

    [Test]
    public async Task Deserialize_EmptyDefaultsGracefully()
    {
        var config = ConfigurationStore.Deserialize("{}");
        await Assert.That(config.Worlds).IsEmpty();
        await Assert.That(config.Version).IsEqualTo(1);
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

public class BeipMuImporterTests
{
    [Test]
    public async Task Import_ReadsWorldsAndAutomation()
    {
        const string xml = """
            <Settings>
              <Worlds>
                <World name="Furry MUCK" host="muck.example.net" port="8888" ssl="false">
                  <Triggers>
                    <Trigger name="page" pattern="pages you" gag="false" send="reply hi" />
                  </Triggers>
                  <Aliases>
                    <Alias name="gt" pattern="^gt (.+)" send="grouptell $1" />
                  </Aliases>
                </World>
                <World name="Secure" host="secure.example.org" port="7777" tls="true" />
              </Worlds>
            </Settings>
            """;

        var worlds = BeipMuImporter.Import(xml);
        await Assert.That(worlds).Count().IsEqualTo(2);

        var muck = worlds[0];
        await Assert.That(muck.Name).IsEqualTo("Furry MUCK");
        await Assert.That(muck.Host).IsEqualTo("muck.example.net");
        await Assert.That(muck.Port).IsEqualTo(8888);
        await Assert.That(muck.Triggers).HasSingleItem();
        await Assert.That(muck.Triggers[0].Actions.SendResponse).IsEqualTo("reply hi");
        await Assert.That(muck.Aliases[0].Substitution).IsEqualTo("grouptell $1");

        await Assert.That(worlds[1].UseTls).IsTrue();
    }

    [Test]
    public async Task Import_InvalidXml_ReturnsEmpty()
    {
        var worlds = BeipMuImporter.Import("not xml <<<");
        await Assert.That(worlds).IsEmpty();
    }
}
