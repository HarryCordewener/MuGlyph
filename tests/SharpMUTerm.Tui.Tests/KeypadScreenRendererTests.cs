using SharpMUTerm.Core.Automation;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class KeypadScreenRendererTests
{
    private static List<Macro> Scene() => new()
    {
        new Macro { Name = "Look", Key = "Num5", Enabled = true, Command = "look" },
        new Macro { Name = "North", Key = "Num8", Enabled = true, Command = "north" },
        new Macro { Name = "Quiet ping", Key = "Ctrl+F1", Enabled = true, Command = "ping" },
        new Macro { Name = "Old macro", Key = "Ctrl+F2", Enabled = false, Command = "who" },
    };

    [Test]
    public async Task Render_NumpadCellShowsCommandBoundToNum5()
    {
        var lines = KeypadScreenRenderer.Render(Scene());
        var middleRow = lines.Single(l => l.Contains("[5]"));
        await Assert.That(middleRow).Contains("look");
    }

    /// <summary>
    /// Every numpad cell is padded to a fixed visible width, so the three columns line up whatever is
    /// bound. Without that, a bound key widens its own cell and shunts the rest of its row right.
    /// </summary>
    [Test]
    public async Task NumpadColumn_ColumnsAlignRegardlessOfBoundCommandLength()
    {
        // Deliberately lopsided: one row long enough to be ellipsised, one row entirely unbound.
        var macros = new List<Macro>
        {
            new() { Key = "Num7", Enabled = true, Command = "northwest" },
            new() { Key = "Num5", Enabled = true, Command = "look" },
            new() { Key = "Num3", Enabled = true, Command = "a very long command indeed" },
        };

        // Skip(1): the first entry is the "NUMPAD" caption, not a key row.
        var offsets = KeypadScreenRenderer.NumpadColumn(macros).Skip(1).Select(CellOffsets).ToList();

        await Assert.That(offsets).HasCount().EqualTo(3);
        await Assert.That(offsets[1]).IsEqualTo(offsets[0]);
        await Assert.That(offsets[2]).IsEqualTo(offsets[0]);
    }

    /// <summary>
    /// The visible columns at which each cell starts, as a comparable string. Strips markup the way
    /// the renderer's own width maths does: an escaped bracket counts as one literal character, a
    /// styling tag as zero. Each cell starts at the literal '[' of its "[n]" marker.
    /// </summary>
    private static string CellOffsets(string row)
    {
        var protectedText = row.Replace("[[", "\u0001").Replace("]]", "\u0002");
        var visible = System.Text.RegularExpressions.Regex
            .Replace(protectedText, @"\[[^\]]*\]", string.Empty)
            .Replace('\u0001', '[')
            .Replace('\u0002', ']');

        var starts = Enumerable.Range(0, visible.Length)
            .Where(i => visible[i] == '[')
            .Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return string.Join(",", starts);
    }

    [Test]
    public async Task Render_UnboundNumpadCellShowsPlaceholder()
    {
        var lines = KeypadScreenRenderer.Render(Scene());
        var topRow = lines.Single(l => l.Contains("[7]"));
        await Assert.That(topRow).Contains("[dim]—[/]");
    }

    [Test]
    public async Task Render_HotkeyListShowsCtrlF1BindingCommandAndEnabledTick()
    {
        var lines = KeypadScreenRenderer.Render(Scene());
        var row = lines.Single(l => l.Contains("Ctrl+F1"));
        await Assert.That(row).Contains("ping");
        await Assert.That(row).Contains("✓");
    }

    [Test]
    public async Task Render_DisabledHotkeyShowsDimMarker()
    {
        var lines = KeypadScreenRenderer.Render(Scene());
        var row = lines.Single(l => l.Contains("Ctrl+F2"));
        await Assert.That(row).Contains("who");
        await Assert.That(row).Contains("[dim]·[/]");
    }

    [Test]
    public async Task Render_EmptyMacrosRendersGridWithNoHotkeys()
    {
        var lines = KeypadScreenRenderer.Render(Array.Empty<Macro>());
        await Assert.That(lines.Any(l => l.Contains("[5]") && l.Contains("[dim]—[/]"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("no hotkeys"))).IsTrue();
    }

    [Test]
    public async Task Render_HeaderContainsF4AndTitle()
    {
        var lines = KeypadScreenRenderer.Render(Scene());
        await Assert.That(lines[0]).Contains("Keypad & hotkeys");
        await Assert.That(lines[0]).Contains("F4");
    }

    [Test]
    public async Task Render_EscapesMarkupBracketsInCommand()
    {
        var macros = new List<Macro>
        {
            new Macro { Name = "Weird", Key = "Ctrl+F9", Enabled = true, Command = "say [hi]" },
        };
        var lines = KeypadScreenRenderer.Render(macros);
        var row = lines.Single(l => l.Contains("Ctrl+F9"));
        await Assert.That(row).Contains("say [[hi]]");
    }
}
