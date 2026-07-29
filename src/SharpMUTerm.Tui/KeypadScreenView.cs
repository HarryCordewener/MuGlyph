using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace SharpMUTerm.Tui;

/// <summary>
/// Composes the F4 Keypad &amp; hotkeys screen from real panels (grids) rather than one merged markup
/// blob: a header band carrying the keyboard hints, a body whose numpad-grid panel and hotkey-list
/// panel are separated by a vertical rule, and a Cancel/Save action bar pinned to the last row. The
/// numpad is the fixed-width column (its 3×3 grid has a natural size), the hotkey list takes the
/// rest. The markup for each panel comes from the pure <see cref="KeypadScreenRenderer"/> so the
/// content stays unit-tested; this only lays it out.
/// </summary>
internal static class KeypadScreenView
{
    /// <summary>
    /// The numpad column's width — its 3×3 grid measures exactly this (three cells of "[[N]] " plus a
    /// command, two gaps between them), so anything wider is slack taken from the binding list beside
    /// it. That list is the one that needs it: a binding row carries four wells and a command, and its
    /// widest state is an armed key capture whose prompt is twice the width of the key it replaces.
    /// </summary>
    private const int NumpadColumnWidth = 48;

    public static IWindowControl Build(
        IReadOnlyList<Macro> macros,
        IReadOnlyList<TriggerSet> sets,
        int selected,
        int width,
        ScreenFocus? focus = null)
    {
        var header = ScreenChrome.Band(
            KeypadScreenRenderer.HeaderLine(width, KeypadScreenRenderer.Model(macros, sets, selected), focus),
            ScreenPalette.HeaderBg);
        var footer = ScreenChrome.Band(
            KeypadScreenRenderer.FooterLine(macros, width, focus, selected), ScreenPalette.FooterBg);

        // Body: numpad grid │ hotkey list, as two real columns.
        var numpadCol = ScreenChrome.Stretch(new MarkupControl(KeypadScreenRenderer.NumpadColumn(macros)));
        var hotkeysCol = ScreenChrome.Stretch(new MarkupControl(
            ScreenChrome.Indent(KeypadScreenRenderer.HotkeysColumn(macros, focus, sets, selected))));
        var body = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Width(NumpadColumnWidth).Add(numpadCol))
            .Column(c => c.Width(1).Add(ScreenChrome.VerticalRule()))
            .Column(c => c.Width(1).Add(ScreenChrome.Filler()))
            .Column(c => c.Flex(1).Add(hotkeysCol))
            .Build();

        // Header on the first row, footer on the last, body taking everything between — so the action
        // bar sits at the bottom of the screen instead of trailing the content.
        var root = Controls.Grid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);
        root.Rows(GridLength.Cells(1), GridLength.Star(1), GridLength.Cells(1)).Columns(GridLength.Star(1));
        root.Place(header, 0, 0, 1, 1);
        root.Place(body, 1, 0, 1, 1);
        root.Place(footer, 2, 0, 1, 1);

        return root.Build();
    }
}
