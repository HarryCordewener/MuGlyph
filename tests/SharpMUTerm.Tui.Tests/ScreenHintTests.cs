using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The header hints against the model they are drawn from. <see cref="ScreenCursorTests"/> pins the
/// rule for <c>⏎ edit</c> and <c>Del remove</c> — a screen may not advertise a key its
/// <see cref="ScreenModel"/> doesn't offer. These are the same rule for the movement keys, in both
/// directions, because the arrows failed it the *other* way round: ←→ changed pane on four screens and
/// nothing anywhere said so.
/// </summary>
public class ScreenHintTests
{
    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new() { Name = "Tell", Pattern = "tells you", Actions = new TriggerActions() },
            },
            Aliases = new List<Alias> { new() { Name = "k", Pattern = "^k$", Substitution = "kill" } },
            Macros = new List<Macro> { new() { Name = "look", Key = "F1", Command = "look" } },
            Timers = new List<TimerDefinition> { new() { Name = "ping", IntervalSeconds = 30, Command = "look" } },
        },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new()
        {
            Name = "Aetherfall",
            Host = "aetherfall.mux",
            Characters = new List<CharacterDefinition> { new() { Name = "Corvid" } },
        },
    };

    /// <summary>The two glyphs a screen uses to claim the sideways keys do something.</summary>
    private const string SidewaysHint = "←→";

    /// <summary>
    /// A screen names ←→ <b>if and only if</b> it has a pane to move sideways into. F4, F7 and F8 are a
    /// single pane and so must stay silent about them; the other five change pane on the key and must
    /// say so, since a movement nobody mentions is one nobody finds — which is precisely how F5's
    /// character fields came to be reachable and unguessable.
    /// </summary>
    [Test]
    public async Task AScreenNamesTheSidewaysKeysExactlyWhenItHasSomewhereToGo()
    {
        var screens = EveryScreen();

        // Both cases must be represented, or an "if and only if" passes vacuously.
        await Assert.That(screens.Any(s => s.Model.PaneCount > 1)).IsTrue();
        await Assert.That(screens.Any(s => s.Model.PaneCount == 1)).IsTrue();

        foreach (var (name, header, model) in screens)
        {
            await Assert.That(header.Contains(SidewaysHint, StringComparison.Ordinal))
                .IsEqualTo(model.PaneCount > 1)
                .Because($"{name} has {model.PaneCount} pane(s)");
        }
    }

    /// <summary>
    /// The hint constants themselves, which is where the rule is actually enforced: the multi-pane form
    /// names both ways of changing pane, the single-pane form names neither.
    /// </summary>
    [Test]
    public async Task TheHintConstantsAgreeWithWhatTheKeysDo()
    {
        await Assert.That(ScreenChrome.ListHints).Contains(SidewaysHint);
        await Assert.That(ScreenChrome.ListHints).Contains("⇥");
        await Assert.That(ScreenChrome.SingleListHints).DoesNotContain(SidewaysHint);
        await Assert.That(ScreenChrome.SingleListHints).DoesNotContain("⇥");
    }

    /// <summary>
    /// While a field is open the arrows belong to the buffer, so the navigation hints go with the rest
    /// of the screen's verbs — leaving ←→ on screen there would name a key that moves a caret.
    /// </summary>
    [Test]
    public async Task TheSidewaysHintGoesAwayWhileAFieldIsOpen()
    {
        var sets = Sets();
        var model = TriggersScreenRenderer.Model(sets, 0);
        var editing = new ScreenFocus(0, 0, new ScreenFieldEdit(0, "Tell", 4, null, RowFields: 2));

        await Assert.That(TriggersScreenRenderer.HeaderLine(0, model)).Contains(SidewaysHint);
        await Assert.That(TriggersScreenRenderer.HeaderLine(0, model, editing)).DoesNotContain(SidewaysHint);
    }

    /// <summary>Every screen's header, built from the very model its live view hands it.</summary>
    private static List<(string Name, string Header, ScreenModel Model)> EveryScreen()
    {
        var sets = Sets();
        var worlds = Worlds();
        var macros = sets.SelectMany(s => s.Macros).ToList();
        var textAnsi = OptionsScreenRenderer.TextAnsiScreen();
        var input = OptionsScreenRenderer.InputScreen();

        var triggers = TriggersScreenRenderer.Model(sets, 0);
        var aliases = AliasesScreenRenderer.Model(sets, 0);
        var keypad = KeypadScreenRenderer.Model(macros, sets, 0);
        var timers = TimersScreenRenderer.Model(sets, 0);
        var world = WorldsScreenRenderer.Model(worlds, sets, 0, 0);
        var text = OptionsScreenRenderer.Model(textAnsi);
        var inputModel = OptionsScreenRenderer.Model(input);

        return new List<(string, string, ScreenModel)>
        {
            ("F2 triggers", TriggersScreenRenderer.HeaderLine(0, triggers), triggers),
            ("F3 aliases", AliasesScreenRenderer.HeaderLine(0, aliases), aliases),
            ("F4 keypad", KeypadScreenRenderer.HeaderLine(0, keypad), keypad),
            ("F5 worlds", WorldsScreenRenderer.HeaderLine(0, world), world),
            ("F6 timers", TimersScreenRenderer.HeaderLine(0, timers), timers),
            ("F7 text & ANSI", OptionsScreenRenderer.HeaderLine(textAnsi.Title, textAnsi.FKey, 0, text), text),
            ("F8 input", OptionsScreenRenderer.HeaderLine(input.Title, input.FKey, 0, inputModel), inputModel),
            (
                "F9 character logging",
                WorldsScreenRenderer.HeaderLine(0, world, null, WorldsScreenRenderer.LogFKey),
                world),
        };
    }
}
