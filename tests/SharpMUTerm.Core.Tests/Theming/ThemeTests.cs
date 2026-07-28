using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Theming;

namespace SharpMUTerm.Core.Tests.Theming;

public class ThemeTests
{
    [Test]
    public async Task Resolve_DefaultColor_UsesThemeForegroundBackground()
    {
        var theme = ThemeLibrary.Dark();
        await Assert.That(theme.Resolve(TerminalColor.Default, isBackground: false)).IsEqualTo(theme.Foreground);
        await Assert.That(theme.Resolve(TerminalColor.Default, isBackground: true)).IsEqualTo(theme.Background);
    }

    [Test]
    public async Task Resolve_RgbColor_IsPassedThrough()
    {
        var theme = ThemeLibrary.Dark();
        await Assert.That(theme.Resolve(TerminalColor.FromRgb(10, 20, 30), false)).IsEqualTo(new Rgb(10, 20, 30));
    }

    [Test]
    public async Task Palette16Override_TakesPrecedence()
    {
        var solarized = ThemeLibrary.SolarizedDark();
        // Solarized red (index 1) differs from the standard xterm #800000.
        var red = solarized.ResolveIndex(1);
        await Assert.That(red).IsEqualTo(new Rgb(0xdc, 0x32, 0x2f));
        await Assert.That(red).IsNotEqualTo(AnsiPalette.ToRgb(1));
    }

    [Test]
    public async Task ResolveIndex_AboveBase_UsesStandardPalette()
    {
        var solarized = ThemeLibrary.SolarizedDark();
        await Assert.That(solarized.ResolveIndex(196)).IsEqualTo(AnsiPalette.ToRgb(196));
    }

    [Test]
    public async Task Library_GetByName_ReturnsBuiltin_OrDarkFallback()
    {
        await Assert.That(ThemeLibrary.Get("Light").Name).IsEqualTo("Light");
        await Assert.That(ThemeLibrary.Get("nonexistent").Name).IsEqualTo("Dark");
    }

    [Test]
    public async Task Theme_RoundTripsThroughConfigJson_AsHex()
    {
        var config = new AppConfiguration { ThemeName = "Custom", Theme = ThemeLibrary.SolarizedDark() };
        config.Theme.Name = "Custom";
        var json = ConfigurationStore.Serialize(config);

        // Colours serialise as hex strings for readable theme files.
        await Assert.That(json).Contains("#002b36");

        var restored = ConfigurationStore.Deserialize(json);
        await Assert.That(restored.Theme.Background).IsEqualTo(new Rgb(0x00, 0x2b, 0x36));
        await Assert.That(restored.Theme.Palette16!).Count().IsEqualTo(16);
    }

    [Test]
    public async Task RgbConverter_ParsesAndFormats()
    {
        await Assert.That(new Rgb(0x12, 0x34, 0x56).ToHex()).IsEqualTo("#123456");
        await Assert.That(RgbJsonConverter.Parse("#abcdef")).IsEqualTo(new Rgb(0xab, 0xcd, 0xef));
        await Assert.That(RgbJsonConverter.Parse("bad")).IsEqualTo(new Rgb(0, 0, 0));
    }
}
