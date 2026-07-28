using SharpMUTerm.Core.Configuration;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace SharpMUTerm.Tui;

/// <summary>
/// Composes the F2 Triggers &amp; spawn routing screen from real panels (grids) rather than one
/// merged markup blob: a header band carrying the keyboard hints, a body whose rule-list panel and
/// editor panel are separated by a vertical rule, and a Cancel/Save action bar pinned to the last
/// row. The markup for each panel comes from the pure <see cref="TriggersScreenRenderer"/> so the
/// content stays unit-tested; this only lays it out.
/// </summary>
internal static class TriggersScreenView
{
    private const int RulesColumnWidth = TriggersScreenRenderer.ColumnWidth;

    public static IWindowControl Build(
        IReadOnlyList<TriggerSet> sets,
        int selectedTrigger,
        IReadOnlyList<string> spawnTargets,
        int width,
        ScreenFocus? focus = null)
    {
        var header = ScreenChrome.Band(TriggersScreenRenderer.HeaderLine(width), ScreenPalette.HeaderBg);
        var footer = ScreenChrome.Band(
            TriggersScreenRenderer.FooterLine(sets, selectedTrigger, width), ScreenPalette.FooterBg);

        // Body: rule list │ editor, as two real columns.
        var rulesCol = ScreenChrome.Stretch(
            new MarkupControl(TriggersScreenRenderer.RulesColumn(sets, selectedTrigger, focus)));
        var editorCol = ScreenChrome.Stretch(new MarkupControl(
            ScreenChrome.Indent(
                TriggersScreenRenderer.EditorColumn(sets, selectedTrigger, spawnTargets, focus))));
        var body = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Width(RulesColumnWidth).Add(rulesCol))
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
