using SharpMUTerm.Core.Automation;
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
    private const string RuleColor = "#3a4257";
    private const int NumpadColumnWidth = 50;

    public static IWindowControl Build(IReadOnlyList<Macro> macros, int width)
    {
        var header = Band(KeypadScreenRenderer.HeaderLine(width), KeypadScreenRenderer.HeaderBg);
        var footer = Band(KeypadScreenRenderer.FooterLine(macros, width), KeypadScreenRenderer.FooterBg);

        // Body: numpad grid │ hotkey list, as two real columns.
        var numpadCol = Stretch(new MarkupControl(KeypadScreenRenderer.NumpadColumn(macros)));
        var hotkeysCol = Stretch(new MarkupControl(Indent(KeypadScreenRenderer.HotkeysColumn(macros))));
        var body = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Width(NumpadColumnWidth).Add(numpadCol))
            .Column(c => c.Width(1).Add(VerticalRule()))
            .Column(c => c.Width(1).Add(new MarkupControl(new List<string>())))
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

    private static MarkupControl Band(string line, string bg) => new(new List<string> { line })
    {
        BackgroundColor = new Color(bg),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static MarkupControl Stretch(MarkupControl control)
    {
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        return control;
    }

    /// <summary>
    /// The one-cell rule between the columns. A <see cref="MarkupControl"/> with no lines measures to
    /// nothing and never paints its background, so the rule is an empty grid instead — a grid's
    /// background covers its whole arranged area, giving a full-height hairline.
    /// </summary>
    private static IWindowControl VerticalRule()
    {
        var rule = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Flex(1).Add(new MarkupControl(new List<string>())))
            .Build();
        rule.BackgroundColor = new Color(RuleColor);
        return rule;
    }

    /// <summary>Prefixes each hotkey row with a space so it doesn't sit flush against the rule.</summary>
    private static List<string> Indent(IEnumerable<string> lines) => lines.Select(l => " " + l).ToList();
}
