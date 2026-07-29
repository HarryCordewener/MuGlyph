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
    public static IWindowControl Build(
        IReadOnlyList<TriggerSet> sets,
        int selectedTrigger,
        IReadOnlyList<string> spawnTargets,
        int width,
        ScreenFocus? focus = null,
        int height = 0)
    {
        var header = ScreenChrome.Band(
            TriggersScreenRenderer.HeaderLine(
                width, TriggersScreenRenderer.Model(sets, selectedTrigger, spawnTargets), focus),
            ScreenPalette.HeaderBg);
        var footer = ScreenChrome.Band(
            TriggersScreenRenderer.FooterLine(sets, selectedTrigger, width, focus), ScreenPalette.FooterBg);

        // Body: rule list │ editor, as two real columns. The split is a function of the screen's width:
        // at 120 the rule list keeps the share it was designed with, and at 100 it gives cells back to
        // the editor rather than letting the attribute legend run off the right-hand edge.
        var rules = ScreenChrome.SplitWidth(
            width,
            TriggersScreenRenderer.ColumnWidth,
            TriggersScreenRenderer.MinColumnWidth,
            TriggersScreenRenderer.MinEditorWidth);
        var body = ScreenChrome.Rows(height);
        var left = TriggersScreenRenderer.RulesColumn(sets, selectedTrigger, focus, rules);
        var right = TriggersScreenRenderer.EditorColumn(
            sets, selectedTrigger, spawnTargets, focus, width <= 0 ? rules : width - rules - ScreenChrome.ColumnDivider, body);

        var rulesCol = ScreenChrome.Stretch(new MarkupControl(ScreenChrome.Window(left, body)));
        var editorCol = ScreenChrome.Stretch(new MarkupControl(
            ScreenChrome.Indent(ScreenChrome.Window(right, body))));
        return ScreenChrome.Split(header, footer, rulesCol, editorCol, rules, Math.Max(left.Count, right.Count), body);
    }
}
