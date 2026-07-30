using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class RailRendererTests
{
    private static readonly TerminalColor Accent = TerminalColor.FromRgb(0x00, 0xf5, 0xb7);

    private static IReadOnlyList<RailRow> Scene() => RailModel.Build(new[]
    {
        new RailWorld("Aetherfall", "aether.example.org", 4000, Accent, new[]
        {
            new RailCharacter("Corvid", "s1", Connected: true, Active: true, Unread: 5, new[]
            {
                new RailWindow("main", "w:main", "left", Unread: 0, HasUnsent: false, Closed: false),
                new RailWindow("#public", "w:public", "right", Unread: 3, HasUnsent: true, Closed: false),
                new RailWindow("log", "w:log", null, Unread: 0, HasUnsent: false, Closed: true),
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
        await Assert.That(pub).Contains(Glyphs.Draft);
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

    // ---- Clickability ----------------------------------------------------------------------
    //
    // The rail's rows carry command ids (RailModelTests pins which); the renderer's job is to turn
    // those into [link=…] spans that a click can hit, without changing a single visible cell.

    /// <summary>
    /// Each destination row is wrapped in its own target, and the rail's chrome is not. The header is
    /// the one row in an expanded rail that must not be clickable.
    /// </summary>
    [Test]
    public async Task Render_WrapsDestinationRowsInTheirTarget()
    {
        var lines = RailRenderer.Render(Scene());

        await Assert.That(lines[0]).DoesNotContain("[link="); // the CONNECTIONS header is chrome
        await Assert.That(lines.Single(l => l.Contains("Corvid"))).Contains("[link=char:s1]");
        await Assert.That(lines.Single(l => l.Contains("Rookery"))).Contains("[link=char:s2]");
        await Assert.That(lines.Single(l => l.Contains("Aetherfall"))).Contains("[link=char:s1]");
        await Assert.That(lines.Single(l => l.Contains("#public"))).Contains("[link=win:w:public]");
        await Assert.That(lines.Single(l => l.Contains("no characters"))).Contains("[link=rail:no-characters:Empties]");
    }

    /// <summary>A window drawn as closed is still a link — the shell answers it by saying so.</summary>
    [Test]
    public async Task Render_ClosedWindowIsStillALink()
    {
        var log = RailRenderer.Render(Scene()).Single(l => l.Contains("closed"));
        await Assert.That(log).Contains("[link=win:w:log]");
    }

    /// <summary>
    /// The collapsed rail's initials are clickable. They were documented as "clicking still switches
    /// character" while the renderer emitted no links at all and the control's LinkClicked was never
    /// wired — a comment describing a feature that did not exist. An initial that does not switch is
    /// the only handle a collapsed rail has, so this is the row that most needed to be true.
    /// </summary>
    [Test]
    public async Task RenderCollapsed_InitialsAreClickable()
    {
        var lines = RailRenderer.RenderCollapsed(Scene());

        await Assert.That(lines.Single(l => l.Contains("C"))).Contains("[link=char:s1]");
        await Assert.That(lines.Single(l => l.Contains("R"))).Contains("[link=char:s2]");
        await Assert.That(lines.Count(l => l.Contains("[link="))).IsEqualTo(4); // 2 worlds + 2 characters
    }

    /// <summary>
    /// The invariant the sidebar's geometry rests on: link markup adds no visible cell. RailWidth is
    /// derived from the widest row's visible width, and every connected session is told its own pane's
    /// size over NAWS — so a link that widened a row by one cell would shrink every pane and misreport
    /// every session's width. Measured with the app's own MarkupWidth, against the same rows rendered
    /// with their targets stripped.
    /// </summary>
    [Test]
    public async Task Render_LinkMarkupDoesNotChangeVisibleWidth()
    {
        var rows = Scene();
        var bareRows = rows.Select(r => r with { Target = null }).ToList();

        var linked = RailRenderer.Render(rows);
        var bare = RailRenderer.Render(bareRows);
        await Assert.That(linked.Any(l => l.Contains("[link="))).IsTrue(); // the test would pass vacuously otherwise
        await AssertSameWidths(linked, bare);

        await AssertSameWidths(RailRenderer.RenderCollapsed(rows), RailRenderer.RenderCollapsed(bareRows));
    }

    private static async Task AssertSameWidths(List<string> linked, List<string> bare)
    {
        await Assert.That(linked.Count).IsEqualTo(bare.Count);
        for (var i = 0; i < linked.Count; i++)
        {
            await Assert.That(SharpMUTermApp.MarkupWidth(linked[i]))
                .IsEqualTo(SharpMUTermApp.MarkupWidth(bare[i]))
                .Because($"row {i} ('{bare[i]}') must measure the same with a link on it as without");
        }
    }

    /// <summary>
    /// A bracket in a world or character name cannot end the link tag early. Both the framework's markup
    /// parser and MarkupWidth read a tag by scanning to the next <c>]</c>, so an unescaped one would
    /// break the link <em>and</em> spill the rest of the target into the row as visible text — which,
    /// through RailWidth, would resize the sidebar.
    /// </summary>
    [Test]
    public async Task Render_EscapesBracketsInsideTheLinkTarget()
    {
        var rows = RailModel.Build(new[]
        {
            new RailWorld("Od]d", "h", 1, Accent, Array.Empty<RailCharacter>()),
        });

        var line = RailRenderer.Render(rows).Single(l => l.Contains("no characters"));

        await Assert.That(line).Contains("[link=rail:no-characters:Od%5Dd]");
        await Assert.That(SharpMUTermApp.MarkupWidth(line)).IsEqualTo("    no characters".Length);
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
