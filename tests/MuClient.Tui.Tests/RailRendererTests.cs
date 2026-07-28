using MuClient.Core.Text;
using MuClient.Core.Workspaces;
using MuClient.Tui;

namespace MuClient.Tui.Tests;

public class RailRendererTests
{
    private static readonly TerminalColor Accent = TerminalColor.FromRgb(0x00, 0xf5, 0xb7);

    private static IReadOnlyList<RailRow> Scene() => RailModel.Build(new[]
    {
        new RailWorld("Aetherfall", "aether.example.org", 4000, Accent, new[]
        {
            new RailCharacter("Corvid", "s1", Connected: true, Active: true, Unread: 5, new[]
            {
                new RailWindow("main", "left", Unread: 0, HasUnsent: false, Closed: false),
                new RailWindow("#public", "right", Unread: 3, HasUnsent: true, Closed: false),
                new RailWindow("log", null, Unread: 0, HasUnsent: false, Closed: true),
            }),
            new RailCharacter("Rookery", "s2", Connected: false, Active: false, Unread: 0,
                Array.Empty<RailWindow>()),
        }),
        new RailWorld("Empties", "void.example.org", 4001, TerminalColor.Default,
            Array.Empty<RailCharacter>()),
    });

    [Test]
    public async Task Render_HeaderFirst()
    {
        var lines = RailRenderer.Render(Scene());
        await Assert.That(lines[0]).Contains("CONNECTIONS");
    }

    [Test]
    public async Task Render_WorldCarriesAccentSpine()
    {
        var lines = RailRenderer.Render(Scene());
        await Assert.That(lines.Any(l => l.Contains("#00f5b7") && l.Contains("Aetherfall"))).IsTrue();
    }

    [Test]
    public async Task Render_ActiveCharacterMarkedAndConnectedDot()
    {
        var lines = RailRenderer.Render(Scene());
        var corvid = lines.Single(l => l.Contains("Corvid"));
        await Assert.That(corvid).Contains("▸");
        await Assert.That(corvid).Contains("●");
        await Assert.That(corvid).Contains("5");
    }

    [Test]
    public async Task Render_InactiveCharacterHasOpenDot_NoWindows()
    {
        var lines = RailRenderer.Render(Scene());
        var rookery = lines.Single(l => l.Contains("Rookery"));
        await Assert.That(rookery).Contains("○");
        // Rookery is inactive, so its (absent) windows never expand — no window rows follow it
        // beyond the active character's own.
        await Assert.That(lines.Count(l => l.Contains("▪"))).IsEqualTo(3);
    }

    [Test]
    public async Task Render_WindowsShowUnsentUnreadAndPane()
    {
        var lines = RailRenderer.Render(Scene());
        var pub = lines.Single(l => l.Contains("#public"));
        await Assert.That(pub).Contains("✎");
        await Assert.That(pub).Contains("3");
        await Assert.That(pub).Contains("right");

        var log = lines.Single(l => l.Contains("log"));
        await Assert.That(log).Contains("closed");
    }

    [Test]
    public async Task Render_EmptyWorldSaysNoCharacters()
    {
        var lines = RailRenderer.Render(Scene());
        await Assert.That(lines.Any(l => l.Contains("no characters"))).IsTrue();
    }

    [Test]
    public async Task RenderCollapsed_ShowsWorldSeparatorsAndCharacterInitials()
    {
        var lines = RailRenderer.RenderCollapsed(Scene());
        // Two world separators (▚), and Corvid's initial with a connected dot + unread count.
        await Assert.That(lines.Count(l => l.Contains("▚"))).IsEqualTo(2);
        await Assert.That(lines.Any(l => l.Contains("●") && l.Contains("C") && l.Contains("5"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("○") && l.Contains("R"))).IsTrue();
    }

    [Test]
    public async Task Render_EscapesMarkupBrackets()
    {
        var rows = RailModel.Build(new[]
        {
            new RailWorld("Aetherfall", "h", 1, Accent, new[]
            {
                new RailCharacter("Cor[vid]", "s1", Connected: true, Active: false, Unread: 0,
                    Array.Empty<RailWindow>()),
            }),
        });

        var lines = RailRenderer.Render(rows);
        await Assert.That(lines.Any(l => l.Contains("Cor[[vid]]"))).IsTrue();
    }
}
