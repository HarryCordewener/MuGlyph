using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace SharpMUTerm.Tui;

/// <summary>
/// Composes an options screen (F7 Text &amp; ANSI, F8 Input &amp; spellcheck, F9 Logging) from real
/// panels rather than one merged markup blob: a header band carrying the keyboard hints, the options
/// list on a single full-width elevated card, and a Cancel/Save action bar pinned to the last row.
/// These screens are one list, not two panes, so there is no column split and no vertical rule — the
/// card is the whole body, sized to its content so it reads as a settings surface under the header
/// rather than a lone column floating on the backdrop. The markup comes from the pure
/// <see cref="OptionsScreenRenderer"/> so the content stays unit-tested; this only lays it out.
/// </summary>
internal static class OptionsScreenView
{
    /// <summary>Blank pad rows above and below the list inside the card.</summary>
    private const int CardPadding = 2;

    /// <summary>Columns the list is inset by: two of padding on each side of the card.</summary>
    private const int CardInset = 4;

    public static IWindowControl Build(
        OptionsScreenRenderer.OptionsScreen screen, int width, ScreenFocus? focus = null)
    {
        var header = ScreenChrome.Band(
            OptionsScreenRenderer.HeaderLine(
                screen.Title, screen.FKey, width, OptionsScreenRenderer.Model(screen), focus),
            ScreenPalette.HeaderBg);
        var footer = ScreenChrome.Band(
            OptionsScreenRenderer.FooterLine(screen.Rows, width), ScreenPalette.FooterBg);

        var rows = OptionsScreenRenderer.BodyColumn(screen.Rows, focus, Math.Max(0, width - CardInset));
        var card = Card(rows);

        // Header on the first row, footer on the last, the card hugging its content directly beneath the
        // header and the remaining space left as backdrop — so the action bar sits at the bottom of the
        // screen instead of trailing the content.
        var root = Controls.Grid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);
        root.Rows(
                GridLength.Cells(1), GridLength.Cells(rows.Count + CardPadding), GridLength.Star(1),
                GridLength.Cells(1))
            .Columns(GridLength.Star(1));
        root.Place(header, 0, 0, 1, 1);
        root.Place(card, 1, 0, 1, 1);
        root.Place(footer, 3, 0, 1, 1);

        return root.Build();
    }

    /// <summary>
    /// The full-width elevated card holding the list. A <see cref="MarkupControl"/> only paints the
    /// cells its lines cover, so the card is a grid — a grid's background covers its whole arranged
    /// area, giving the band its full width and the padding rows their colour.
    /// </summary>
    private static IWindowControl Card(IReadOnlyList<string> rows)
    {
        var list = new MarkupControl(Pad(rows)) { HorizontalAlignment = HorizontalAlignment.Stretch };
        var card = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Flex(1).Add(list))
            .Build();
        card.BackgroundColor = new Color(ScreenPalette.EditBg);
        return card;
    }

    /// <summary>Insets the list one blank row from the header and two columns from the left edge.</summary>
    private static List<string> Pad(IReadOnlyList<string> rows)
    {
        var lines = new List<string> { string.Empty };
        lines.AddRange(rows.Select(r => "  " + r));
        return lines;
    }
}
