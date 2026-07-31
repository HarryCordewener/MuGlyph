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
                new RailWindow("main", "w:main", "⌥1", 0, false, false),
                new RailWindow("#public", "w:public", "⌥2", 3, true, false),
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
            new RailCharacter("Corvid", "k1", true, Active: true, 0, new[] { new RailWindow("main", "w:main", "⌥1", 0, false, false) }),
            new RailCharacter("Rookery", "k2", false, Active: false, 0, new[] { new RailWindow("hidden", "w:hidden", "⌥9", 0, false, false) }),
        });

        var rows = RailModel.Build(new[] { world });
        var windows = rows.Where(r => r.Kind == RailRowKind.Window).ToArray();

        await Assert.That(windows).HasSingleItem();
        await Assert.That(windows[0].Label).IsEqualTo("main");
    }

    [Test]
    public async Task Window_CarriesUnsentUnreadAndItsChord()
    {
        var world = new RailWorld("W", "h", 1, Accent, new[]
        {
            new RailCharacter("C", "k", true, true, 3, new[] { new RailWindow("#public", "w:public", "⌥2", 3, HasUnsent: true, Closed: false) }),
        });

        var win = RailModel.Build(new[] { world }).Single(r => r.Kind == RailRowKind.Window);
        await Assert.That(win.Unsent).IsTrue();
        await Assert.That(win.Unread).IsEqualTo(3);
        await Assert.That(win.Chord).IsEqualTo("⌥2");
    }

    [Test]
    public async Task WorldWithNoCharacters_PrintsNoCharacters()
    {
        var world = new RailWorld("Empty", "h", 1, Accent, Array.Empty<RailCharacter>());
        var rows = RailModel.Build(new[] { world });
        await Assert.That(rows.Any(r => r.Kind == RailRowKind.Empty && r.Label == "no characters")).IsTrue();
    }

    // ---- Click targets ---------------------------------------------------------------------
    //
    // The rail is clickable, and what each row does when clicked is a property of the projection, not
    // of the view: the rows are rebuilt on every refresh, so a payload derived from anything other than
    // the model (a row index into a previous render, say) would go stale the moment a world connected.

    /// <summary>A character row names the command that switches to it — the id the ⌃P surface uses.</summary>
    [Test]
    public async Task CharacterRow_TargetsItsOwnSessionKey()
    {
        var rows = RailModel.Build(new[] { TwoCharacterWorld() });

        var corvid = rows.Single(r => r.Kind == RailRowKind.Character && r.Label == "Corvid");
        var rookery = rows.Single(r => r.Kind == RailRowKind.Character && r.Label == "Rookery");

        await Assert.That(corvid.Target).IsEqualTo("char:Aetherfall.Corvid");
        await Assert.That(rookery.Target).IsEqualTo("char:Aetherfall.Rookery");
    }

    /// <summary>A window row names the command that activates that window, by workspace id.</summary>
    [Test]
    public async Task WindowRow_TargetsItsWindowId()
    {
        var rows = RailModel.Build(new[] { TwoCharacterWorld() });

        var windows = rows.Where(r => r.Kind == RailRowKind.Window).ToArray();
        await Assert.That(windows.Length).IsEqualTo(2);
        await Assert.That(windows[0].Target).IsEqualTo("win:main");
        await Assert.That(windows[1].Target).IsEqualTo("win:spawn:#public");
    }

    /// <summary>
    /// A closed window still names its window. The shell answers a "go to" for a window no pane holds by
    /// saying it is not open any more — the same answer the ⌃P entry for that window gives, because it
    /// is the same id — which is what the row's own "closed" label already promises.
    /// </summary>
    [Test]
    public async Task ClosedWindowRow_StillTargetsItsWindowId()
    {
        var world = new RailWorld("W", "h", 1, Accent, new[]
        {
            new RailCharacter("C", "W.C", true, true, 0, new[]
            {
                new RailWindow("log", "spawn:log", null, 0, HasUnsent: false, Closed: true),
            }),
        });

        var row = RailModel.Build(new[] { world }).Single(r => r.Kind == RailRowKind.Window);

        await Assert.That(row.Closed).IsTrue();
        await Assert.That(row.Target).IsEqualTo("win:spawn:log");
    }

    /// <summary>
    /// The header is chrome, not a destination, and so is a row kind the rail never draws as one. A
    /// target on either would make a click do something the row does not offer.
    /// </summary>
    [Test]
    public async Task HeaderRow_HasNoTarget()
    {
        var rows = RailModel.Build(new[] { TwoCharacterWorld() });
        await Assert.That(rows[0].Kind).IsEqualTo(RailRowKind.Header);
        await Assert.That(rows[0].Target).IsNull();
    }

    /// <summary>
    /// A world is not connectable on its own, so clicking one goes to the character you are already in.
    /// </summary>
    [Test]
    public async Task WorldRow_TargetsItsActiveCharacter()
    {
        var rows = RailModel.Build(new[] { TwoCharacterWorld() });
        var world = rows.Single(r => r.Kind == RailRowKind.World);
        await Assert.That(world.Target).IsEqualTo("char:Aetherfall.Corvid");
    }

    /// <summary>With none of its characters active, the world row prefers a connected one over the first.</summary>
    [Test]
    public async Task WorldRow_PrefersAConnectedCharacterWhenNoneIsActive()
    {
        var world = new RailWorld("W", "h", 1, Accent, new[]
        {
            new RailCharacter("Offline", "W.Offline", Connected: false, Active: false, 0, Array.Empty<RailWindow>()),
            new RailCharacter("Live", "W.Live", Connected: true, Active: false, 0, Array.Empty<RailWindow>()),
        });

        var row = RailModel.Build(new[] { world }).Single(r => r.Kind == RailRowKind.World);
        await Assert.That(row.Target).IsEqualTo("char:W.Live");
    }

    /// <summary>With nothing active and nothing connected, the first character is still somewhere to go.</summary>
    [Test]
    public async Task WorldRow_FallsBackToItsFirstCharacter()
    {
        var world = new RailWorld("W", "h", 1, Accent, new[]
        {
            new RailCharacter("First", "W.First", false, false, 0, Array.Empty<RailWindow>()),
            new RailCharacter("Second", "W.Second", false, false, 0, Array.Empty<RailWindow>()),
        });

        var row = RailModel.Build(new[] { world }).Single(r => r.Kind == RailRowKind.World);
        await Assert.That(row.Target).IsEqualTo("char:W.First");
    }

    /// <summary>
    /// A world with nothing to switch to still answers a click. Both the world row and the
    /// "no characters" row under it carry the rail's own report target — a click that did nothing at all
    /// would be the third surface found this week promising something it does not do.
    /// </summary>
    [Test]
    public async Task WorldWithNoCharacters_TargetsAReportRatherThanNothing()
    {
        var rows = RailModel.Build(new[]
        {
            new RailWorld("Empties", "h", 1, Accent, Array.Empty<RailCharacter>()),
        });

        var world = rows.Single(r => r.Kind == RailRowKind.World);
        var empty = rows.Single(r => r.Kind == RailRowKind.Empty);

        await Assert.That(world.Target).IsEqualTo("rail:no-characters:Empties");
        await Assert.That(empty.Target).IsEqualTo("rail:no-characters:Empties");
    }

    /// <summary>Every row the rail draws either goes somewhere or is inert. Nothing may be half-wired.</summary>
    [Test]
    public async Task EveryRow_IsEitherATargetOrDeclaredChrome()
    {
        var rows = RailModel.Build(new[] { TwoCharacterWorld(), new RailWorld("Empties", "h", 1, Accent, Array.Empty<RailCharacter>()) });

        foreach (var row in rows)
        {
            var expected = row.Kind is RailRowKind.Header or RailRowKind.Host;
            await Assert.That(row.Target is null).IsEqualTo(expected)
                .Because($"a {row.Kind} row ('{row.Label}') must {(expected ? "not " : string.Empty)}be clickable");
        }
    }

    private static RailWorld TwoCharacterWorld() => new("Aetherfall", "aetherfall.mux", 4201, Accent, new[]
    {
        new RailCharacter("Corvid", "Aetherfall.Corvid", Connected: true, Active: true, Unread: 3, new[]
        {
            new RailWindow("main", "main", "⌥1", 0, false, false),
            new RailWindow("#public", "spawn:#public", "⌥2", 3, true, false),
        }),
        new RailCharacter("Rookery", "Aetherfall.Rookery", Connected: false, Active: false, Unread: 0,
            Array.Empty<RailWindow>()),
    });
}
