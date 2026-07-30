using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class WorldsScreenRendererTests
{
    private static readonly TerminalColor Accent = TerminalColor.FromRgb(0x00, 0xf5, 0xb7);

    private static List<TriggerSet> TriggerSets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Description = "channel + page routing",
            Triggers = new List<Trigger>
            {
                new() { Pattern = "tells you" },
                new() { Pattern = "pages" },
            },
        },
        new TriggerSet
        {
            Name = "Combat",
            Description = "hp/damage tracking",
            Triggers = new List<Trigger> { new() { Pattern = "hits you" } },
        },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new WorldDefinition
        {
            Name = "Aetherfall",
            Host = "aether.example.org",
            Port = 4000,
            UseTls = true,
            AllowInvalidCertificates = false,
            Encoding = "UTF-8",
            KeepaliveSeconds = 30,
            Accent = Accent,
            Characters = new List<CharacterDefinition>
            {
                new()
                {
                    Name = "Corvid",
                    Password = "secret",
                    OnConnect = "look;score",
                    TriggerSets = new List<string> { "Comms", "Combat" },
                },
                new()
                {
                    Name = "Rookery",
                    TriggerSets = new List<string> { "Comms" },
                },
            },
        },
        new WorldDefinition
        {
            Name = "Voidmoor",
            Host = "void.example.org",
            Port = 4001,
            UseTls = false,
            Encoding = "ISO-8859-1",
            KeepaliveSeconds = 0,
            Accent = TerminalColor.Default,
            Characters = new List<CharacterDefinition>(),
        },
    };

    [Test]
    public async Task Render_WorldsListedWithCounts()
    {
        var lines = WorldsScreenRenderer.Render(Worlds(), TriggerSets(), selectedWorld: 0, selectedCharacter: 0);

        await Assert.That(lines.Any(l => l.Contains("Aetherfall"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("2 chars"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("Voidmoor"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("0 chars"))).IsTrue();
    }

    [Test]
    public async Task Render_SelectedWorldFieldsShown()
    {
        var lines = WorldsScreenRenderer.Render(Worlds(), TriggerSets(), selectedWorld: 0, selectedCharacter: 0);
        var text = string.Join("\n", lines);

        await Assert.That(text).Contains("aether.example.org");
        await Assert.That(text).Contains("4000");
        await Assert.That(text).Contains("UTF-8");
        await Assert.That(text).Contains("30s");

        // TLS is stated by the checkbox that sets it, and only there. This used to read
        // Contains("TLS on"), which the detail column's title strip satisfied — a strip that also
        // repeated the world's name, its host:port and its encoding, each of them directly above the
        // editable row carrying the same value. The claim is now the stronger one: the flag is on
        // screen, in the one place it can be changed.
        await Assert.That(lines.Count(l => l.Contains("TLS"))).IsEqualTo(1);
        await Assert.That(lines.Single(l => l.Contains("TLS"))).Contains("encrypt this connection");
        await Assert.That(text).DoesNotContain("TLS on");
    }

    /// <summary>
    /// The detail column opens on the world's editable rows, with no summary of them above. Every token
    /// of the strip that used to sit there — <c>Aetherfall  aether.example.org:4000  TLS on · UTF-8</c>
    /// — was repeated in the five rows immediately underneath, and the address a third time in the
    /// WORLDS list beside it.
    /// </summary>
    [Test]
    public async Task Render_TheDetailColumnDoesNotRestateTheWorldAboveItsFields()
    {
        var column = WorldsScreenRenderer.DetailColumn(
            Worlds(), TriggerSets(), selectedWorld: 0, selectedCharacter: 0, ScreenPalette.Accent);

        await Assert.That(column[0]).Contains("WORLD");
        await Assert.That(column.Count(l => l.Contains("aether.example.org"))).IsEqualTo(1);
        await Assert.That(column.Count(l => l.Contains("UTF-8"))).IsEqualTo(1);
    }

    /// <summary>
    /// The world's two security flags, as the two checkboxes that replaced the read-only line
    /// summarising them — which is now the only place on the screen the flags are stated at all.
    /// </summary>
    [Test]
    public async Task Render_SecurityIsTwoCheckboxesUnderTheSecurityLabel()
    {
        var lines = WorldsScreenRenderer.Render(Worlds(), TriggerSets(), selectedWorld: 0, selectedCharacter: 0);
        var tls = lines.Single(l => l.Contains("security") && l.Contains("TLS"));
        var certificates = lines.Single(l => l.Contains("accept invalid certificates"));

        await Assert.That(tls).Contains("[[x]]");
        await Assert.That(certificates).Contains("[[ ]]");
        await Assert.That(lines.Any(l => l.Contains("certs strict"))).IsFalse();
    }

    /// <summary>
    /// The certificate row while validation is actually off: the warn ink and the <c>▲</c> a refused
    /// value gets, and a consequence rather than a restatement of the label.
    /// </summary>
    [Test]
    public async Task Render_TurningOffCertificateValidationIsDrawnAsAWarning()
    {
        var worlds = Worlds();
        worlds[0].AllowInvalidCertificates = true;

        var row = WorldsScreenRenderer.Render(worlds, TriggerSets(), 0, 0)
            .Single(l => l.Contains("accept invalid certificates"));

        await Assert.That(row).Contains("[[x]]");
        await Assert.That(row).Contains(ScreenPalette.Warn);
        await Assert.That(row).Contains("▲ anyone can impersonate this host");
    }

    /// <summary>
    /// A character's log settings, in that character's own form. This is what the F9 Logging screen
    /// used to draw for whichever character happened to be active, without naming it.
    /// </summary>
    [Test]
    public async Task Render_CharacterFormShowsThatCharactersLog()
    {
        var worlds = Worlds();
        worlds[0].Characters[0].Logging = new LoggingSettings
        {
            Format = LogFormat.Html,
            Directory = "/var/log/mu",
        };

        var lines = WorldsScreenRenderer.Render(worlds, TriggerSets(), selectedWorld: 0, selectedCharacter: 0);

        await Assert.That(lines.Any(l => l.Contains("CHARACTER · Corvid"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("log") && l.Contains("Html"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("log folder") && l.Contains("/var/log/mu"))).IsTrue();

        // The row says whose settings these are, for the reader who arrived by the F9 they used to
        // live behind — where the same two values silently belonged to somebody.
        await Assert.That(lines.Any(l => l.Contains("this character only"))).IsTrue();
    }

    /// <summary>
    /// The other character's log, on the same screen, showing its own values: the second half of "whose
    /// settings are these" is that picking a different character shows a different answer.
    /// </summary>
    [Test]
    public async Task Render_AnUnsetLogReadsAsNoneAndTheDefaultFolder()
    {
        var lines = WorldsScreenRenderer.Render(Worlds(), TriggerSets(), selectedWorld: 0, selectedCharacter: 1);

        await Assert.That(lines.Any(l => l.Contains("CHARACTER · Rookery"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("log") && l.Contains(LogFormat.None.ToString()))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("log folder")
            && l.Contains(WorldsScreenRenderer.DefaultDirectory))).IsTrue();
    }

    /// <summary>
    /// The CHARACTERS list is a selector: it names the characters and marks the selected one, and says
    /// nothing else. It used to be a four-column table — <c>name  state  login  trigger sets</c> — whose
    /// other three columns were all drawn again on the same screen at the same time: the session state
    /// and the login mode are rows of the CHARACTER form directly underneath, and the sets are the pane
    /// beside it with checkboxes, descriptions and counts.
    /// </summary>
    [Test]
    public async Task Render_TheCharacterListSelectsAndTheFormHoldsTheDetail()
    {
        var column = WorldsScreenRenderer.DetailColumn(
            Worlds(), TriggerSets(), selectedWorld: 0, selectedCharacter: 0, ScreenPalette.Accent);

        // Not the [⧉ duplicate] chip, and not the Del row that names the same character as its target.
        var corvid = column.Single(l =>
            l.Contains("Corvid")
            && !l.Contains("[[")
            && !l.Contains(ScreenChrome.RemovesWord));
        await Assert.That(corvid).Contains("▸");
        await Assert.That(column.Any(l => l.Contains("Rookery"))).IsTrue();

        // None of the three restated columns survives in the list, nor the header that named them.
        foreach (var gone in new[] { "○ offline", "login", "manual", "trigger sets", "state" })
        {
            await Assert.That(column.Any(l => l.Contains(gone))).IsFalse().Because(gone + " is the form's");
        }

        // And each of them is still on the screen, in the form that owns it.
        var form = WorldsScreenRenderer.FormColumn(Worlds()[0].Characters[0], ScreenPalette.Accent);
        await Assert.That(form.Any(l => l.Contains("login"))).IsTrue();
        await Assert.That(form.Any(l => l.Contains("session"))).IsTrue();
    }

    [Test]
    public async Task Render_EmptyWorldShowsNoCharactersMessage()
    {
        var lines = WorldsScreenRenderer.Render(Worlds(), TriggerSets(), selectedWorld: 1, selectedCharacter: 0);

        await Assert.That(lines.Any(l => l.Contains("no characters — this world has nothing to connect with.")))
            .IsTrue();
    }

    [Test]
    public async Task Render_CharacterDetailChecklistMarksAssignedSets()
    {
        var lines = WorldsScreenRenderer.Render(Worlds(), TriggerSets(), selectedWorld: 0, selectedCharacter: 0);

        var comms = lines.Single(l => l.Contains("▪ Comms"));
        await Assert.That(comms).Contains("[[x]]");
        await Assert.That(comms).Contains("2 rules");

        var combat = lines.Single(l => l.Contains("▪ Combat"));
        await Assert.That(combat).Contains("[[x]]");
        await Assert.That(combat).Contains("1 rules");
    }

    [Test]
    public async Task Render_UnassignedSetShowsEmptyCheckbox()
    {
        // Rookery only has "Comms" assigned, not "Combat".
        var lines = WorldsScreenRenderer.Render(Worlds(), TriggerSets(), selectedWorld: 0, selectedCharacter: 1);

        var combat = lines.Single(l => l.Contains("▪ Combat"));
        await Assert.That(combat).Contains("[[ ]]");
    }

    [Test]
    public async Task Render_NoWorldsShowsHeaderAndEmptyMessage()
    {
        var lines = WorldsScreenRenderer.Render(
            Array.Empty<WorldDefinition>(), TriggerSets(), selectedWorld: -1, selectedCharacter: -1);

        await Assert.That(lines.Any(l => l.Contains("WORLDS"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("no worlds"))).IsTrue();
    }

    [Test]
    public async Task Render_EscapesMarkupBrackets()
    {
        var worlds = new List<WorldDefinition>
        {
            new()
            {
                Name = "Weird[World]",
                Host = "h",
                Port = 1,
                Characters = new List<CharacterDefinition>(),
            },
        };

        var lines = WorldsScreenRenderer.Render(worlds, TriggerSets(), selectedWorld: 0, selectedCharacter: 0);
        await Assert.That(lines.Any(l => l.Contains("Weird[[World]]"))).IsTrue();
    }
}
