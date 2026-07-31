using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet.Mssp;

namespace SharpMUTerm.Tui;

/// <summary>
/// Composes <see cref="MsspScreenRenderer"/>'s blocks into the control tree the settings overlay hosts.
/// One column, not two: this screen is a report rather than a list beside an editor, so there is no
/// second pane to divide, and the hairline every other screen carries would be dividing nothing.
/// </summary>
internal static class MsspScreenView
{
    internal static IWindowControl Build(
        WorldDefinition? world,
        MsspObservation? observation,
        DateTimeOffset now,
        int width,
        ScreenFocus? focus = null,
        int height = 0)
    {
        var header = ScreenChrome.Band(MsspScreenRenderer.HeaderLine(width), ScreenPalette.HeaderBg);
        var footer = ScreenChrome.Band(
            MsspScreenRenderer.FooterLine(world, observation, focus, width, now), ScreenPalette.FooterBg);

        var rows = ScreenChrome.Rows(height);
        var body = ScreenChrome.Stretch(new MarkupControl(
            MsspScreenRenderer.Render(world, observation, now, focus, rows, width)));

        var root = Controls.Grid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);

        if (rows <= 0)
        {
            root.Rows(GridLength.Cells(1), GridLength.Star(1), GridLength.Cells(1)).Columns(GridLength.Star(1));
            root.Place(header, 0, 0, 1, 1);
            root.Place(body, 1, 0, 1, 1);
            root.Place(footer, 2, 0, 1, 1);
            return root.Build();
        }

        // The body is sized to its content and the slack below it belongs to the backdrop, the same call
        // ScreenChrome.Split makes: a report of six rows should not be drawn as a thirty-row empty panel
        // whose emptiness reads as missing data on a screen whose whole subject is what is missing.
        root.Rows(
                GridLength.Cells(1),
                GridLength.Cells(Math.Clamp(
                    MsspScreenRenderer.Render(world, observation, now, focus, rows, width).Count, 1, rows)),
                GridLength.Star(1),
                GridLength.Cells(1))
            .Columns(GridLength.Star(1));
        root.Place(header, 0, 0, 1, 1);
        root.Place(body, 1, 0, 1, 1);
        root.Place(footer, 3, 0, 1, 1);
        return root.Build();
    }
}
