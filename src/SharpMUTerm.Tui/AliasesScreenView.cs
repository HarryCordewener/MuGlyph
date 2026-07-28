using SharpMUTerm.Core.Configuration;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace SharpMUTerm.Tui;

/// <summary>
/// Composes the F3 Aliases screen from real panels (grids) rather than one merged markup blob: a
/// header band carrying the keyboard hints, a body whose alias-list panel and editor panel are
/// separated by a vertical rule, and a Cancel/Save action bar pinned to the last row. The markup for
/// each panel comes from the pure <see cref="AliasesScreenRenderer"/> so the content stays
/// unit-tested; this only lays it out.
/// </summary>
internal static class AliasesScreenView
{
    private const int ListColumnWidth = AliasesScreenRenderer.ColumnWidth;

    public static IWindowControl Build(
        IReadOnlyList<TriggerSet> sets, int selected, int width, ScreenFocus? focus = null)
    {
        var header = ScreenChrome.Band(AliasesScreenRenderer.HeaderLine(width), ScreenPalette.HeaderBg);
        var footer = ScreenChrome.Band(AliasesScreenRenderer.FooterLine(sets, selected, width), ScreenPalette.FooterBg);

        // Body: alias list │ editor, as two real columns.
        var listCol = ScreenChrome.Stretch(
            new MarkupControl(AliasesScreenRenderer.ListColumn(sets, selected, focus)));
        var editorCol = ScreenChrome.Stretch(
            new MarkupControl(ScreenChrome.Indent(AliasesScreenRenderer.EditorColumn(sets, selected, focus))));
        var body = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Width(ListColumnWidth).Add(listCol))
            .Column(c => c.Width(1).Add(ScreenChrome.VerticalRule()))
            .Column(c => c.Width(1).Add(ScreenChrome.Filler()))
            .Column(c => c.Flex(1).Add(editorCol))
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
