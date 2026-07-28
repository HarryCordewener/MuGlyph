using MuClient.Core.Automation;
using MuClient.Tui;

namespace MuClient.Tui.Tests;

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
