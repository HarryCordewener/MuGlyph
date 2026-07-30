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

    // --- The focus planes ------------------------------------------------------------------------

    /// <summary>
    /// How far apart two tones have to be before this suite will call them distinguishable. Well above
    /// <see cref="Visible"/>, because the complaint these tests exist for was about a pair that <em>did</em>
    /// technically differ: the two input bands were thirteen points per channel apart, which is a luma step
    /// of about fifteen, and the report was that the difference is "almost invisible". So the bar for
    /// <em>focus</em> is deliberately set above the step that was reported as not good enough.
    /// </summary>
    private const double Obvious = 24;

    /// <summary>
    /// The measured distance between the two input bands, on every shipped theme. This is the assertion
    /// that speaks the language of the bug report — not "a colour was set" but "the two colours are this
    /// far apart" — and the number it is held to is above the one that was reported as too close.
    /// </summary>
    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    [Arguments("Solarized Dark")]
    public async Task TheArmedBandIsObviouslyBrighterThanTheIdleOne(string themeName)
    {
        var theme = ThemeLibrary.Get(themeName);
        var armed = Luma(WorkspacePalette.ArmedBand(theme));
        var idle = Luma(WorkspacePalette.IdleBand(theme));

        await Assert.That(armed - idle).IsGreaterThan(Obvious);
    }

    /// <summary>
    /// What the old pair actually measured, kept as the floor the new one has to beat. Without it
    /// "obvious" is a number in a constant with nothing behind it; with it, the suite records the step
    /// that was rejected and holds the replacement to more than double it.
    /// </summary>
    [Test]
    public async Task TheNewStepIsMoreThanTwiceTheOneThatWasReportedAsInvisible()
    {
        var theme = ThemeLibrary.Get("Dark");
        var reported = Luma(new SharpMUTerm.Core.Text.Rgb(0x33, 0x39, 0x4c))
            - Luma(new SharpMUTerm.Core.Text.Rgb(0x26, 0x2b, 0x3a));
        var now = Luma(WorkspacePalette.ArmedBand(theme)) - Luma(WorkspacePalette.IdleBand(theme));

        await Assert.That(now).IsGreaterThan(reported * 2);
    }

    /// <summary>
    /// The armed band differs in hue as well as in brightness, so the pair still reads for someone who
    /// cannot use the luma step — the second cue colour is not asked to carry alone.
    /// </summary>
    [Test]
    [Arguments("Dark")]
    [Arguments("Solarized Dark")]
    public async Task TheArmedBandLeansTowardThePromptColour(string themeName)
    {
        var theme = ThemeLibrary.Get(themeName);
        var armed = WorkspacePalette.ArmedBand(theme);
        var idle = WorkspacePalette.IdleBand(theme);

        // The prompt colour is bluer than it is red on every shipped theme, so the band it leans toward
        // gains more blue than red. Asserting the *direction* rather than a hex keeps this a claim about
        // the relationship, which is the only thing that survives a theme change.
        await Assert.That(armed.B - idle.B).IsGreaterThan(armed.R - idle.R);
    }

    /// <summary>
    /// A focused pane's plane is a step above an unfocused one, on every theme. Smaller than the input
    /// bands' step on purpose — the pane's plane is what the game's own colours are read against — which
    /// is exactly why the tab chip and the ▌ marker carry the rest of the signal.
    /// </summary>
    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    [Arguments("Solarized Dark")]
    public async Task TheFocusedPaneIsLiftedOffTheUnfocusedOne(string themeName)
    {
        var theme = ThemeLibrary.Get(themeName);
        var focused = Luma(WorkspacePalette.Focus(theme));
        var unfocused = Luma(WorkspacePalette.Surface(theme));

        await Assert.That(focused - unfocused).IsGreaterThan(Visible);
    }

    /// <summary>
    /// The four planes of the workspace are four <em>different</em> colours. This is not pedantry: the
    /// first attempt at this made the idle input band equal to the backdrop, which reads as the bar
    /// vanishing into the chrome — and made a painted-cell test unable to tell one from the other, which
    /// is the same defect stated in the language of the frame.
    /// </summary>
    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    [Arguments("Solarized Dark")]
    public async Task EveryPlaneIsItsOwnTone(string themeName)
    {
        var theme = ThemeLibrary.Get(themeName);
        var planes = new[]
        {
            WorkspacePalette.Surface(theme),
            WorkspacePalette.Backdrop(theme),
            WorkspacePalette.Focus(theme),
            WorkspacePalette.IdleBand(theme),
            WorkspacePalette.ArmedBand(theme),
        };

        await Assert.That(planes.Distinct().Count()).IsEqualTo(planes.Length);
    }

    /// <summary>
    /// <b>Truecolor is not guaranteed.</b> On a 256-colour terminal every one of these tones is
    /// approximated to the nearest xterm-256 entry, and two colours a few points apart collapse onto the
    /// <em>same</em> entry — which is very likely part of why the old pair of bands was reported as
    /// indistinguishable rather than merely close — two tones a few points apart can round to one entry,
    /// and nothing in the old design guaranteed they would not. The distinction has to survive that
    /// quantisation, so the pairs that carry focus are checked against the palette a 256-colour terminal
    /// actually has, on every shipped theme.
    /// </summary>
    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    [Arguments("Solarized Dark")]
    public async Task FocusSurvivesA256ColourTerminal(string themeName)
    {
        var theme = ThemeLibrary.Get(themeName);

        await Assert.That(Xterm256(WorkspacePalette.ArmedBand(theme)))
            .IsNotEqualTo(Xterm256(WorkspacePalette.IdleBand(theme)));
        await Assert.That(Xterm256(WorkspacePalette.Focus(theme)))
            .IsNotEqualTo(Xterm256(WorkspacePalette.Surface(theme)));
    }

    /// <summary>
    /// The nearest xterm-256 palette entry to a colour — the 6×6×6 cube (16–231) and the 24-step grey
    /// ramp (232–255), which is what a 256-colour terminal has to choose from. Nearest by squared
    /// distance, which is what every terminal and library that does this uses.
    /// </summary>
    private static int Xterm256(SharpMUTerm.Core.Text.Rgb rgb)
    {
        var levels = new[] { 0, 95, 135, 175, 215, 255 };
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < 216; i++)
        {
            var r = levels[i / 36];
            var g = levels[i / 6 % 6];
            var b = levels[i % 6];
            Consider(16 + i, r, g, b);
        }

        for (var i = 0; i < 24; i++)
        {
            var grey = 8 + (i * 10);
            Consider(232 + i, grey, grey, grey);
        }

        return best;

        void Consider(int index, int r, int g, int b)
        {
            var distance = ((rgb.R - r) * (rgb.R - r)) + ((rgb.G - g) * (rgb.G - g)) + ((rgb.B - b) * (rgb.B - b));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }
    }
}
