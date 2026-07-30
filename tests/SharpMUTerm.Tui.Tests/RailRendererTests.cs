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

    // --- the width trap: nothing volatile may cost a cell ------------------------------------------

    /// <summary>
    /// <b>The unsent-draft pen costs the same whether it is there or not.</b> The reported defect: the pen
    /// was emitted only when a window held a draft, so a row grew by two cells on the first keystroke of
    /// every line — and <c>RailWidth</c> takes the sidebar's column count from its widest row, so the
    /// sidebar grew, the panes shrank, and per-pane NAWS re-announced a new terminal size to every
    /// connected server. Asserted on <see cref="SharpMUTermApp.MarkupWidth"/> because that is the measure
    /// the layout is derived from; anything else could agree here and disagree where it matters.
    /// </summary>
    [Test]
    public async Task Render_AnUnsentDraftDoesNotChangeARowsWidth()
    {
        var rows = Scene();
        var without = rows.Select(r => r with { Unsent = false }).ToList();
        var with = rows.Select(r => r with { Unsent = r.Kind == RailRowKind.Window }).ToList();

        var drawn = RailRenderer.Render(with);
        await Assert.That(drawn.Any(l => l.Contains(Glyphs.Draft, StringComparison.Ordinal))).IsTrue();

        await AssertSameWidths(drawn, RailRenderer.Render(without));
    }

    /// <summary>
    /// And the unread count, which is the worse of the two because it arrives unbidden from the wire: a
    /// badge appearing on a background window, and the same badge going from one digit to two, both used
    /// to resize the sidebar on a line of output nobody asked for. Every count is checked against the same
    /// row with none — including one past the cap, which is where a reserved field would otherwise burst.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(99)]
    [Arguments(100)]
    [Arguments(12345)]
    public async Task Render_AnUnreadCountDoesNotChangeARowsWidth(int unread)
    {
        var rows = Scene();
        var none = rows.Select(r => r with { Unread = 0 }).ToList();
        var some = rows.Select(r => r with { Unread = unread }).ToList();

        await AssertSameWidths(RailRenderer.Render(some), RailRenderer.Render(none));
        await AssertSameWidths(RailRenderer.RenderCollapsed(some), RailRenderer.RenderCollapsed(none));
    }

    /// <summary>A count past the cap says so rather than growing: three cells, always.</summary>
    [Test]
    public async Task Render_ALargeUnreadCountIsCapped()
    {
        var rows = Scene().Select(r => r with { Unread = 4321 }).ToList();
        await Assert.That(RailRenderer.Render(rows).Any(l => l.Contains("99+", StringComparison.Ordinal)))
            .IsTrue();
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
