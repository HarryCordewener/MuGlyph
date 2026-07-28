using MuClient.Core.Configuration;
using MuClient.Core.Text;
using MuClient.Tui;

namespace MuClient.Tui.Tests;

public class WorldSettingsRendererTests
{
    private static WorldDefinition World() => new()
    {
        Name = "Aetherfall",
        Host = "aetherfall.mux",
        Port = 4201,
        UseTls = true,
        ContentFormat = ContentFormat.Mxp,
        Emoji = new EmojiSettings { Enabled = true, Emoticons = true, Shortcodes = false },
        Accent = TerminalColor.FromRgb(0x00, 0xf5, 0xb7),
        Characters =
        {
            new CharacterDefinition { Name = "Corvid", AutoLogin = true, TriggerSets = { "default" } },
            new CharacterDefinition { Name = "Rookery" },
        },
    };

    [Test]
    public async Task Render_ShowsConnectionAndRenderingValues()
    {
        var lines = WorldSettingsRenderer.Render(World(), TerminalColor.FromRgb(0x00, 0xf5, 0xb7));
        var text = string.Join("\n", lines);

        await Assert.That(text).Contains("WORLD SETTINGS");
        await Assert.That(text).Contains("aetherfall.mux");
        await Assert.That(text).Contains("4201");
        await Assert.That(lines.Any(l => l.Contains("TLS") && l.Contains("on"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("Format") && l.Contains("Mxp"))).IsTrue();
    }

    [Test]
    public async Task Render_ShowsAccentSwatchWithHex()
    {
        var lines = WorldSettingsRenderer.Render(World(), TerminalColor.FromRgb(0x00, 0xf5, 0xb7));
        await Assert.That(lines.Any(l => l.Contains("████") && l.Contains("#00f5b7"))).IsTrue();
    }

    [Test]
    public async Task Render_ListsCharactersWithLoginAndSets()
    {
        var lines = WorldSettingsRenderer.Render(World(), TerminalColor.FromRgb(0x00, 0xf5, 0xb7));
        var corvid = lines.Single(l => l.Contains("Corvid"));
        await Assert.That(corvid).Contains("auto-login");
        await Assert.That(corvid).Contains("default");

        var rookery = lines.Single(l => l.Contains("Rookery"));
        await Assert.That(rookery).Contains("manual");
    }

    [Test]
    public async Task Render_EmojiSummaryReflectsSubsettings()
    {
        var lines = WorldSettingsRenderer.Render(World(), TerminalColor.FromRgb(0x00, 0xf5, 0xb7));
        var emoji = lines.Single(l => l.Contains("Emoji"));
        await Assert.That(emoji).Contains("emoticons on");
        await Assert.That(emoji).Contains("shortcodes off");
    }

    [Test]
    public async Task Render_EmptyWorldSaysNoCharacters()
    {
        var world = new WorldDefinition { Name = "Void" };
        var lines = WorldSettingsRenderer.Render(world, TerminalColor.Default);
        await Assert.That(lines.Any(l => l.Contains("no characters"))).IsTrue();
    }
}
