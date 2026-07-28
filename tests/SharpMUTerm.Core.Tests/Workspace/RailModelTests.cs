using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

public class RailModelTests
{
    private static readonly TerminalColor Accent = TerminalColor.FromRgb(0, 245, 183);

    [Test]
    public async Task Build_EmitsHeaderThenWorldAndCharacters()
    {
        var world = new RailWorld("Aetherfall", "aetherfall.mux", 4201, Accent, new[]
        {
            new RailCharacter("Corvid", "Aetherfall.Corvid", Connected: true, Active: true, Unread: 3, new[]
            {
                new RailWindow("main", "p1", 0, false, false),
                new RailWindow("#public", "p2", 3, true, false),
            }),
            new RailCharacter("Rookery", "Aetherfall.Rookery", Connected: false, Active: false, Unread: 0, Array.Empty<RailWindow>()),
        });

        var rows = RailModel.Build(new[] { world });

        await Assert.That(rows[0].Kind).IsEqualTo(RailRowKind.Header);
        await Assert.That(rows[1].Kind).IsEqualTo(RailRowKind.World);
        await Assert.That(rows[1].Label).IsEqualTo("Aetherfall");
        await Assert.That(rows[1].Accent).IsEqualTo(Accent);
        // The address line is intentionally omitted from the rail (worlds show name + characters only).
        await Assert.That(rows.Any(r => r.Kind == RailRowKind.Host)).IsFalse();
        await Assert.That(rows[2].Kind).IsEqualTo(RailRowKind.Character);
        await Assert.That(rows[2].Active).IsTrue();
        await Assert.That(rows[2].Unread).IsEqualTo(3);
    }

    [Test]
    public async Task Windows_AreExpandedOnlyUnderTheActiveCharacter()
    {
        var world = new RailWorld("Aetherfall", "h", 1, Accent, new[]
        {
            new RailCharacter("Corvid", "k1", true, Active: true, 0, new[] { new RailWindow("main", "p1", 0, false, false) }),
            new RailCharacter("Rookery", "k2", false, Active: false, 0, new[] { new RailWindow("hidden", "p9", 0, false, false) }),
        });

        var rows = RailModel.Build(new[] { world });
        var windows = rows.Where(r => r.Kind == RailRowKind.Window).ToArray();

        await Assert.That(windows).HasSingleItem();
        await Assert.That(windows[0].Label).IsEqualTo("main");
    }

    [Test]
    public async Task Window_CarriesUnsentUnreadAndPane()
    {
        var world = new RailWorld("W", "h", 1, Accent, new[]
        {
            new RailCharacter("C", "k", true, true, 3, new[] { new RailWindow("#public", "p2", 3, HasUnsent: true, Closed: false) }),
        });

        var win = RailModel.Build(new[] { world }).Single(r => r.Kind == RailRowKind.Window);
        await Assert.That(win.Unsent).IsTrue();
        await Assert.That(win.Unread).IsEqualTo(3);
        await Assert.That(win.Pane).IsEqualTo("p2");
    }

    [Test]
    public async Task WorldWithNoCharacters_PrintsNoCharacters()
    {
        var world = new RailWorld("Empty", "h", 1, Accent, Array.Empty<RailCharacter>());
        var rows = RailModel.Build(new[] { world });
        await Assert.That(rows.Any(r => r.Kind == RailRowKind.Empty && r.Label == "no characters")).IsTrue();
    }
}
