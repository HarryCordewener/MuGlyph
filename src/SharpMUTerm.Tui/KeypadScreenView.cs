using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpConsoleUI;
using SharpConsoleUI.Controls;

namespace SharpMUTerm.Tui;

/// <summary>
/// Composes the F4 Keypad &amp; hotkeys screen from real panels (grids) rather than one merged markup
/// blob: a header band carrying the keyboard hints, a body whose numpad-grid panel and hotkey-list
/// panel are separated by a vertical rule, and a Cancel/Save action bar pinned to the last row. The
/// markup for each panel comes from the pure <see cref="KeypadScreenRenderer"/> so the content stays
/// unit-tested; this only lays it out (through <see cref="ScreenChrome.Split"/>, which every
/// two-column screen shares).
/// <para>
/// The numpad column asks for the width its longest bound command needs
/// (<see cref="KeypadScreenRenderer.NumpadWidth"/>) instead of the fixed 48 it used to take, and gives
/// cells back when the binding list would otherwise lose its commands off the edge. A constant here
/// was what let the diagram draw <c>[[1]] look at a…</c> beside the same command in full.
/// </para>
/// </summary>
internal static class KeypadScreenView
{
    public static IWindowControl Build(
        IReadOnlyList<Macro> macros,
        IReadOnlyList<TriggerSet> sets,
        int selected,
        int width,
        ScreenFocus? focus = null,
        int height = 0)
    {
        var header = ScreenChrome.Band(
            KeypadScreenRenderer.HeaderLine(width, KeypadScreenRenderer.Model(macros, sets, selected), focus),
            ScreenPalette.HeaderBg);
        var footer = ScreenChrome.Band(
            KeypadScreenRenderer.FooterLine(macros, width, focus, selected), ScreenPalette.FooterBg);

        // While a key capture is armed the binding row swaps its key well for a prompt twice the width,
        // so the list asks for more and the diagram gives it up for as long as the prompt is up. The
        // numpad is the one thing on this screen with nothing to do with the keystroke being waited for.
        var numpad = ScreenChrome.SplitWidth(
            width,
            KeypadScreenRenderer.NumpadWidth(macros),
            KeypadScreenRenderer.MinNumpadWidth,
            focus?.Edit is { Capture: true }
                ? KeypadScreenRenderer.CaptureWidth
                : KeypadScreenRenderer.MinHotkeysWidth);
        var rows = ScreenChrome.Rows(height);
        var left = KeypadScreenRenderer.NumpadColumn(macros, numpad);
        var right = KeypadScreenRenderer.HotkeysColumn(
            macros, focus, sets, selected, width <= 0 ? numpad : width - numpad - ScreenChrome.ColumnDivider);

        var numpadCol = ScreenChrome.Stretch(new MarkupControl(ScreenChrome.Window(left, rows)));
        var hotkeysCol = ScreenChrome.Stretch(
            new MarkupControl(ScreenChrome.Indent(ScreenChrome.Window(right, rows))));
        return ScreenChrome.Split(
            header, footer, numpadCol, hotkeysCol, numpad, Math.Max(left.Count, right.Count), rows);
    }
}
