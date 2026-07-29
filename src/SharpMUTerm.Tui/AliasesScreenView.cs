using SharpMUTerm.Core.Configuration;
using SharpConsoleUI;
using SharpConsoleUI.Controls;

namespace SharpMUTerm.Tui;

/// <summary>
/// Composes the F3 Aliases screen from real panels (grids) rather than one merged markup blob: a
/// header band carrying the keyboard hints, a body whose alias-list panel and editor panel are
/// separated by a vertical rule, and a Cancel/Save action bar pinned to the last row. The markup for
/// each panel comes from the pure <see cref="AliasesScreenRenderer"/> so the content stays
/// unit-tested; this only lays it out (through <see cref="ScreenChrome.Split"/>, which every
/// two-column screen shares).
/// </summary>
internal static class AliasesScreenView
{
    public static IWindowControl Build(
        IReadOnlyList<TriggerSet> sets, int selected, int width, ScreenFocus? focus = null, int height = 0)
    {
        var header = ScreenChrome.Band(
            AliasesScreenRenderer.HeaderLine(width, AliasesScreenRenderer.Model(sets, selected), focus),
            ScreenPalette.HeaderBg);
        var footer = ScreenChrome.Band(
            AliasesScreenRenderer.FooterLine(sets, selected, width, focus), ScreenPalette.FooterBg);

        var list = ScreenChrome.SplitWidth(
            width,
            AliasesScreenRenderer.ColumnWidth,
            AliasesScreenRenderer.MinColumnWidth,
            AliasesScreenRenderer.MinEditorWidth);
        var rows = ScreenChrome.Rows(height);
        var left = AliasesScreenRenderer.ListColumn(sets, selected, focus, list);
        var right = AliasesScreenRenderer.EditorColumn(
            sets, selected, focus, width <= 0 ? list : width - list - ScreenChrome.ColumnDivider, rows);

        var listCol = ScreenChrome.Stretch(new MarkupControl(ScreenChrome.Window(left, rows)));
        var editorCol = ScreenChrome.Stretch(
            new MarkupControl(ScreenChrome.Indent(ScreenChrome.Window(right, rows))));
        return ScreenChrome.Split(
            header, footer, listCol, editorCol, list, Math.Max(left.Count, right.Count), rows);
    }
}
