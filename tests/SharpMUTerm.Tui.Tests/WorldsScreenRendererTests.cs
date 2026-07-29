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
                    AutoLogin = true,
                    OnConnect = "look;score",
                    TriggerSets = new List<string> { "Comms", "Combat" },
                },
                new()
                {
                    Name = "Rookery",
                    AutoLogin = false,
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
        await Assert.That(text).Contains("TLS on");
    }

    /// <summary>
    /// The world's two security flags, as the two checkboxes that replaced the read-only line
    /// summarising them. The world summary at the top of the column still reads <c>TLS on</c>, which is
    /// where a one-glance answer belongs — the rows are where it is changed.
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

    [Test]
    public async Task Render_CharactersTableShowsOfflineAndLoginMode()
    {
        var lines = WorldsScreenRenderer.Render(Worlds(), TriggerSets(), selectedWorld: 0, selectedCharacter: 0);

        var corvid = lines.Single(l => l.Contains("Corvid") && l.Contains("○ offline"));
        await Assert.That(corvid).Contains("auto-login");

        var rookery = lines.Single(l => l.Contains("Rookery") && l.Contains("○ offline"));
        await Assert.That(rookery).Contains("manual");
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
