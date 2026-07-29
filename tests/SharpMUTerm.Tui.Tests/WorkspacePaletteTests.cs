using SharpMUTerm.Core.Theming;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The workspace's three planes. What is pinned here is the <em>relationship</em> between them —
/// which is the whole design: an output pane has to read as a raised surface on every theme, not
/// only on the one the hexes were picked against.
/// </summary>
public class WorkspacePaletteTests
{
    private static double Luma(SharpMUTerm.Core.Text.Rgb rgb) =>
        ((rgb.R * 299) + (rgb.G * 587) + (rgb.B * 114)) / 1000.0;

    /// <summary>A step the eye can find on a terminal's worth of flat colour.</summary>
    private const double Visible = 8;

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    [Arguments("Solarized Dark")]
    public async Task EveryThemeGetsARaisedSurfaceOverARecessedBackdrop(string themeName)
    {
        var theme = ThemeLibrary.Get(themeName);
        var surface = Luma(WorkspacePalette.Surface(theme));
        var backdrop = Luma(WorkspacePalette.Backdrop(theme));

        // The pane is lighter than what surrounds it, or an empty pane goes on reading as a hole.
        await Assert.That(surface - backdrop).IsGreaterThan(Visible);
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    [Arguments("Solarized Dark")]
    public async Task TheHairlineIsFoundAgainstBothPlanes(string themeName)
    {
        var theme = ThemeLibrary.Get(themeName);
        var rule = Luma(WorkspacePalette.Rule(theme));

        // A divider has the backdrop beside it down the side of the rail and the surface beside it
        // between two panes; it has to be visible against each. The direction is the theme's business
        // (lighter than both on a dark theme, between them on a light one) — the distance is not.
        await Assert.That(Math.Abs(rule - Luma(WorkspacePalette.Surface(theme)))).IsGreaterThan(Visible);
        await Assert.That(Math.Abs(rule - Luma(WorkspacePalette.Backdrop(theme)))).IsGreaterThan(Visible);
    }

    [Test]
    public async Task TheSurfaceCarriesTheThemesOwnChromeTint()
    {
        var theme = ThemeLibrary.Get("Dark"); // background #1e1e1e, status background #2d2d3f

        var surface = WorkspacePalette.Surface(theme);

        // A quarter of the way from the text background toward the chrome the header and input bands
        // are already painted in — so the output plane belongs to this application, not to the terminal.
        await Assert.That(surface.R).IsEqualTo((byte)0x22);
        await Assert.That(surface.G).IsEqualTo((byte)0x22);
        await Assert.That(surface.B).IsEqualTo((byte)0x26);
        await Assert.That(surface.B).IsGreaterThan(surface.R); // cooler than the plain background, which is neutral
    }

    [Test]
    public async Task TheBackdropSitsTheSameStepBelowTheSurfaceThatTheSettingsScreensUse()
    {
        // ScreenPalette's own pair: PanelBg is the backdrop an EditBg card floats on. The workspace
        // reuses that step, which is why F5 and the pane behind it read as one application.
        var settingsStep = (Ratio(0x17, 0x1d) + Ratio(0x1b, 0x23) + Ratio(0x24, 0x33)) / 3.0;

        var theme = ThemeLibrary.Get("Dark");
        var surface = WorkspacePalette.Surface(theme);
        var backdrop = WorkspacePalette.Backdrop(theme);
        var workspaceStep = (Ratio(backdrop.R, surface.R) + Ratio(backdrop.G, surface.G) + Ratio(backdrop.B, surface.B)) / 3.0;

        await Assert.That(Math.Abs(workspaceStep - settingsStep)).IsLessThan(0.02);

        static double Ratio(int backdrop, int surface) => backdrop / (double)surface;
    }

    [Test]
    public async Task ADifferentThemeMovesAllThreeTones()
    {
        var dark = ThemeLibrary.Get("Dark");
        var solarized = ThemeLibrary.Get("Solarized Dark");

        await Assert.That(WorkspacePalette.Surface(solarized)).IsNotEqualTo(WorkspacePalette.Surface(dark));
        await Assert.That(WorkspacePalette.Backdrop(solarized)).IsNotEqualTo(WorkspacePalette.Backdrop(dark));
        await Assert.That(WorkspacePalette.Rule(solarized)).IsNotEqualTo(WorkspacePalette.Rule(dark));
    }

    [Test]
    public async Task TheRuleStopsShortOfTheThemesBorderColour()
    {
        var theme = ThemeLibrary.Get("Dark");

        // A hairline that landed on Theme.Border reads fine once and shouts at four panes.
        await Assert.That(Luma(WorkspacePalette.Rule(theme))).IsLessThan(Luma(theme.Border));
    }

    [Test]
    public async Task ThePlanesAreThemesNotConstants()
    {
        // A hand-written theme's background is what the panes are built from, not a hex in this file.
        var custom = new Theme
        {
            Background = new SharpMUTerm.Core.Text.Rgb(0x10, 0x00, 0x20),
            StatusBackground = new SharpMUTerm.Core.Text.Rgb(0x30, 0x00, 0x60),
        };

        var surface = WorkspacePalette.Surface(custom);
        await Assert.That(surface.B).IsGreaterThan(surface.R);
        await Assert.That(surface.G).IsEqualTo((byte)0);
    }
}
