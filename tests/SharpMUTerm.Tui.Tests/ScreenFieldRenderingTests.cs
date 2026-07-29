using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Where an open field edit is drawn. A screen whose state machine is right and whose renderer shows
/// nothing is still unusable, so each screen is asserted to put the buffer and the caret on the line
/// where that field's value already lives — and, just as importantly, to draw exactly what it always
/// drew when nothing is being typed.
/// </summary>
public class ScreenFieldRenderingTests
{
    /// <summary>The block caret: the ink colour on the accent (see <see cref="ScreenPalette"/>).</summary>
    private static readonly string Caret = $"[{ScreenPalette.Ink} on {ScreenPalette.Accent}]";

    private static bool HasCaret(string line) => line.Contains(Caret, StringComparison.Ordinal);

    private static int Carets(IEnumerable<string> lines) => lines.Count(HasCaret);

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger> { new() { Name = "Tell", Pattern = "tells you" } },
            Aliases = new List<Alias> { new() { Name = "k", Pattern = "^k$", Substitution = "kill $1\nsay done" } },
            Macros = new List<Macro> { new() { Name = "look", Key = "Num5", Command = "look" } },
            Timers = new List<TimerDefinition> { new() { Name = "ping", IntervalSeconds = 30, Command = "look" } },
        },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new()
        {
            Name = "Aardwolf",
            Host = "aardmud.org",
            Port = 4000,
            Characters = new List<CharacterDefinition> { new() { Name = "Kaz", OnConnect = "look" } },
        },
    };

    private static ScreenFocus Edit(int pane, int index, int field, string text, string? error = null) =>
        new(pane, index, new ScreenFieldEdit(field, text, text.Length, error));

    [Test]
    public async Task NoOpenEdit_DrawsNoCaretOnAnyScreen()
    {
        var sets = Sets();
        var worlds = Worlds();

        await Assert.That(Carets(TriggersScreenRenderer.EditorColumn(sets, 0, Array.Empty<string>()))).IsEqualTo(0);
        await Assert.That(Carets(AliasesScreenRenderer.EditorColumn(sets, 0))).IsEqualTo(0);
        await Assert.That(Carets(TimersScreenRenderer.EditorColumn(sets, 0))).IsEqualTo(0);
        await Assert.That(Carets(KeypadScreenRenderer.HotkeysColumn(sets[0].Macros))).IsEqualTo(0);
        await Assert.That(Carets(
            WorldsScreenRenderer.DetailColumn(worlds, sets, 0, 0, ScreenPalette.Accent))).IsEqualTo(0);
        await Assert.That(Carets(
            OptionsScreenRenderer.BodyColumn(OptionsScreenRenderer.TextAnsiScreen().Rows))).IsEqualTo(0);
    }

    [Test]
    public async Task Worlds_DrawsTheBufferOnTheFieldsOwnLine_AndNowhereElse()
    {
        var lines = WorldsScreenRenderer.DetailColumn(
            Worlds(), Sets(), 0, 0, ScreenPalette.Accent, Edit(0, 0, 1, "aardmud.net"));

        await Assert.That(Carets(lines)).IsEqualTo(1);
        var edited = lines.Single(HasCaret);
        await Assert.That(edited).Contains("host");
        await Assert.That(edited).Contains("aardmud.ne");

        // The name line is a different field of the same row, so it stays committed. (The world's
        // label column is right-aligned, which is what tells it apart from the characters header.)
        await Assert.That(lines.Single(l => l.Contains("     name[/]", StringComparison.Ordinal)))
            .Contains("Aardwolf");
    }

    [Test]
    public async Task Worlds_PortAndKeepaliveGetTheirOwnCarets()
    {
        var worlds = Worlds();

        var port = WorldsScreenRenderer.DetailColumn(
            worlds, Sets(), 0, 0, ScreenPalette.Accent, Edit(0, 0, 2, "42")).Single(HasCaret);
        await Assert.That(port).Contains("port");

        var keepalive = WorldsScreenRenderer.DetailColumn(
            worlds, Sets(), 0, 0, ScreenPalette.Accent, Edit(0, 0, 4, "30")).Single(HasCaret);
        await Assert.That(keepalive).Contains("keepalive");
    }

    [Test]
    public async Task Worlds_TheCharacterFormEditsTheCharacterRowsFields()
    {
        var character = Worlds()[0].Characters[0];

        var name = WorldsScreenRenderer.FormColumn(character, ScreenPalette.Accent, Edit(1, 0, 0, "Kazimir"), 0)
            .Single(HasCaret);
        await Assert.That(name).Contains("Kazimi");

        var onConnect = WorldsScreenRenderer
            .FormColumn(character, ScreenPalette.Accent, Edit(1, 0, 1, "look;score"), 0)
            .Single(HasCaret);
        await Assert.That(onConnect).Contains("on connect");
    }

    [Test]
    public async Task Triggers_DrawsThePatternBufferUnderItsOwnLabel()
    {
        var lines = TriggersScreenRenderer.EditorColumn(
            Sets(), 0, Array.Empty<string>(), Edit(0, 0, TriggersScreenRenderer.PatternField, "pages you"));

        await Assert.That(Carets(lines)).IsEqualTo(1);
        var edited = lines.Single(HasCaret);
        await Assert.That(edited).Contains("pages yo");
        await Assert.That(lines[lines.IndexOf(edited) - 1]).Contains("match pattern");
    }

    /// <summary>
    /// The name is the first field of every list row, so it is the one ⏎ opens — and the editor pane
    /// has to draw it, or ⏎ on a freshly added rule would type into a value nothing on screen shows.
    /// </summary>
    [Test]
    public async Task Triggers_DrawsTheNameBufferUnderItsOwnLabel()
    {
        var lines = TriggersScreenRenderer.EditorColumn(
            Sets(), 0, Array.Empty<string>(), Edit(0, 0, TriggersScreenRenderer.NameField, "Whisper"));

        await Assert.That(Carets(lines)).IsEqualTo(1);
        var edited = lines.Single(HasCaret);
        await Assert.That(edited).Contains("Whispe");
        await Assert.That(lines[lines.IndexOf(edited) - 1]).Contains("name");
    }

    [Test]
    public async Task Aliases_DrawsTheNamePatternAndCollapsesTheExpansionToTheRowBeingTyped()
    {
        var sets = Sets();

        var name = AliasesScreenRenderer.EditorColumn(sets, 0, Edit(0, 0, AliasesScreenRenderer.NameField, "kk"));
        await Assert.That(Carets(name)).IsEqualTo(1);
        await Assert.That(name.Single(HasCaret)).Contains("kk");

        var pattern = AliasesScreenRenderer.EditorColumn(
            sets, 0, Edit(0, 0, AliasesScreenRenderer.PatternField, "^kk$"));
        await Assert.That(Carets(pattern)).IsEqualTo(1);
        await Assert.That(pattern.Single(HasCaret)).Contains("^kk$");

        // Off-edit the expansion lists one command per line; being typed it is one escaped row.
        await Assert.That(AliasesScreenRenderer.EditorColumn(sets, 0).Count(l => l.Contains("say done"))).IsEqualTo(1);

        var expansion = AliasesScreenRenderer.EditorColumn(
            sets, 0, Edit(0, 0, AliasesScreenRenderer.ExpansionField, @"kill $1\nsay done"));
        await Assert.That(Carets(expansion)).IsEqualTo(1);
        await Assert.That(expansion.Single(HasCaret)).Contains(@"kill $1\nsay don");
    }

    [Test]
    public async Task Timers_DrawsTheNameIntervalAndCommandBuffers()
    {
        var sets = Sets();

        var name = TimersScreenRenderer.EditorColumn(sets, 0, Edit(0, 0, TimersScreenRenderer.NameField, "pong"));
        await Assert.That(Carets(name)).IsEqualTo(1);
        await Assert.That(name.Single(HasCaret)).Contains("pon");

        var interval = TimersScreenRenderer.EditorColumn(
            sets, 0, Edit(0, 0, TimersScreenRenderer.IntervalField, "45"));
        await Assert.That(Carets(interval)).IsEqualTo(1);
        await Assert.That(interval.Single(HasCaret)).Contains("45");

        var command = TimersScreenRenderer.EditorColumn(
            sets, 0, Edit(0, 0, TimersScreenRenderer.CommandField, "score"));
        await Assert.That(command.Single(HasCaret)).Contains("scor");
    }

    /// <summary>
    /// F4 has no editor pane, so both of a binding's editable values are drawn — and typed — in the
    /// binding row itself, on either side of the key it is bound to.
    /// </summary>
    [Test]
    public async Task Keypad_DrawsTheNameAndCommandBuffersInTheBindingRowItself()
    {
        var macros = Sets()[0].Macros;

        var command = KeypadScreenRenderer.HotkeysColumn(
            macros, Edit(0, 0, KeypadScreenRenderer.CommandField, "look here"));

        await Assert.That(Carets(command)).IsEqualTo(1);
        var edited = command.Single(HasCaret);
        await Assert.That(edited).Contains("Num5");
        await Assert.That(edited).Contains("look her");

        var name = KeypadScreenRenderer.HotkeysColumn(
            macros, Edit(0, 0, KeypadScreenRenderer.NameField, "Survey"));

        await Assert.That(Carets(name)).IsEqualTo(1);
        await Assert.That(name.Single(HasCaret)).Contains("Surve");
    }

    [Test]
    public async Task Options_DrawsTheBufferOnTheValueRowUnderTheCursor()
    {
        var screen = OptionsScreenRenderer.LoggingScreen(new LoggingSettings { Format = LogFormat.Plain });

        // Navigable row 0 is "format"; row 1 is "directory".
        var format = OptionsScreenRenderer.BodyColumn(screen.Rows, Edit(0, 0, 0, "Html"));
        await Assert.That(Carets(format)).IsEqualTo(1);
        await Assert.That(format.Single(HasCaret)).Contains("format");

        var directory = OptionsScreenRenderer.BodyColumn(screen.Rows, Edit(0, 1, 0, "/logs"));
        await Assert.That(directory.Single(HasCaret)).Contains("directory");
    }

    [Test]
    public async Task ARefusedValueIsMarkedOnTheRowThatRefusedIt()
    {
        var lines = WorldsScreenRenderer.DetailColumn(
            Worlds(), Sets(), 0, 0, ScreenPalette.Accent,
            Edit(0, 0, 2, "99999", "port must be a whole number 1-65535"));

        var edited = lines.Single(HasCaret);
        await Assert.That(edited).Contains(ScreenPalette.Warn);
        await Assert.That(edited).Contains("port must be a whole number 1-65535");
    }

    [Test]
    public async Task BracketsTypedIntoABufferAreEscaped_NotRenderedAsMarkup()
    {
        var lines = KeypadScreenRenderer.HotkeysColumn(
            Sets()[0].Macros, Edit(0, 0, KeypadScreenRenderer.CommandField, "say [red]hi"));

        var edited = lines.Single(HasCaret);
        await Assert.That(edited).Contains("[[red]]hi");
    }
}
