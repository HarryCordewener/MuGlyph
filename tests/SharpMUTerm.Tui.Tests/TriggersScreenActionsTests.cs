using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The half of a trigger that F2 used to hide. <see cref="TriggerActions"/> can rewrite a line, answer
/// it, restyle it and call a script, and the README has always advertised all four — but only gag,
/// highlight and spawn-route had any UI, so three of the six advertised actions were unreachable
/// without hand-editing the JSON. These pin what each new field writes, what it refuses, that Esc puts
/// the old value back, and — the sharp edge — that flipping case sensitivity drops the compiled matcher
/// the way <see cref="Trigger.Pattern"/> already did.
/// </summary>
public class TriggersScreenActionsTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static readonly string[] Targets = { "Chat" };

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new()
                {
                    Name = "Tell",
                    Pattern = @"^(\w+) tells you (.*)$",
                    Actions = new TriggerActions
                    {
                        SpawnTarget = "Chat",
                        AddAttributes = TextAttributes.Bold,
                        Rewrite = "» $1: $2",
                        SendResponse = "page $1=busy",
                        ScriptCallback = "onTell",
                    },
                },
                new() { Name = "Spam", Pattern = "guild", Actions = new TriggerActions() },
            },
            Aliases = new List<Alias>
            {
                new() { Name = "gr", Pattern = "^gr$", Substitution = "greet", ScriptCallback = "onGreet" },
            },
            Timers = new List<TimerDefinition>
            {
                new() { Name = "tick", IntervalSeconds = 30, Command = "score", ScriptCallback = "onTick" },
            },
        },
    };

    private static ScreenField Field(IReadOnlyList<TriggerSet> sets, int rule, int ordinal) =>
        TriggersScreenRenderer.Model(sets, rule, Targets).FieldAt(0, rule, ordinal)!.Value;

    // ---- rewrite / respond / script -------------------------------------------------------------

    [Test]
    public async Task TheThreeActionTemplatesReadWhatTheRuleAlreadyDoes()
    {
        var sets = Sets();

        await Assert.That(Field(sets, 0, TriggersScreenRenderer.RewriteField).Get()).IsEqualTo("» $1: $2");
        await Assert.That(Field(sets, 0, TriggersScreenRenderer.ResponseField).Get()).IsEqualTo("page $1=busy");
        await Assert.That(Field(sets, 0, TriggersScreenRenderer.ScriptField).Get()).IsEqualTo("onTell");
    }

    /// <summary>
    /// All three land on the rule and are kept. The <c>undo puts it back</c> half of this test went with
    /// the screen-wide revert: a committed action is confirmed work, and only deletions are reviewed on the
    /// way out (see <see cref="ScreenEdits"/>).
    /// </summary>
    [Test]
    public async Task WritingARewriteRespondOrScript_LandsOnTheRule()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[1];
        var edits = new ScreenEdits();

        await Assert.That(edits.Apply(Field(sets, 1, TriggersScreenRenderer.RewriteField), "[guild] $0")).IsNull();
        await Assert.That(edits.Apply(Field(sets, 1, TriggersScreenRenderer.ResponseField), "gtell hi")).IsNull();
        await Assert.That(edits.Apply(Field(sets, 1, TriggersScreenRenderer.ScriptField), "onGuild")).IsNull();

        await Assert.That(trigger.Actions.Rewrite).IsEqualTo("[guild] $0");
        await Assert.That(trigger.Actions.SendResponse).IsEqualTo("gtell hi");
        await Assert.That(trigger.Actions.ScriptCallback).IsEqualTo("onGuild");

        edits.Revert();

        await Assert.That(trigger.Actions.Rewrite).IsEqualTo("[guild] $0");
        await Assert.That(trigger.Actions.SendResponse).IsEqualTo("gtell hi");
        await Assert.That(trigger.Actions.ScriptCallback).IsEqualTo("onGuild");
    }

    /// <summary>
    /// Blank means "this rule does not rewrite", and it is stored as null rather than as <c>""</c> —
    /// <see cref="TriggerEngine"/> tests <c>SendResponse</c> and <c>ScriptCallback</c> with
    /// <c>IsNullOrEmpty</c>, so an empty string would be a second spelling of "off" living in config,
    /// and the two would eventually disagree about which one the screen shows.
    /// </summary>
    [Test]
    public async Task ClearingAnActionStoresNullRatherThanAnEmptyString()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var edits = new ScreenEdits();

        edits.Apply(Field(sets, 0, TriggersScreenRenderer.RewriteField), string.Empty);
        edits.Apply(Field(sets, 0, TriggersScreenRenderer.ResponseField), "   ");
        edits.Apply(Field(sets, 0, TriggersScreenRenderer.ScriptField), string.Empty);

        await Assert.That(trigger.Actions.Rewrite).IsNull();
        await Assert.That(trigger.Actions.SendResponse).IsNull();
        await Assert.That(trigger.Actions.ScriptCallback).IsNull();

        edits.Revert();
        await Assert.That(trigger.Actions.Rewrite).IsNull(); // cleared, and kept cleared
    }

    /// <summary>
    /// A rewrite becomes one output line and a response becomes one command; a newline inside either
    /// would smuggle a second one past the model that counts them.
    /// </summary>
    [Test]
    public async Task AnActionCarryingControlCharactersIsRefusedAndWritesNothing()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];

        foreach (var ordinal in new[]
        {
            TriggersScreenRenderer.RewriteField,
            TriggersScreenRenderer.ResponseField,
            TriggersScreenRenderer.ScriptField,
        })
        {
            var field = Field(sets, 0, ordinal);
            await Assert.That(field.Validate("one\ntwo")).IsNotNull().Because("ordinal " + ordinal);
            await Assert.That(new ScreenEdits().Apply(field, "one\ttwo")).IsNotNull().Because("ordinal " + ordinal);
        }

        await Assert.That(trigger.Actions.Rewrite).IsEqualTo("» $1: $2");
        await Assert.That(trigger.Actions.SendResponse).IsEqualTo("page $1=busy");
        await Assert.That(trigger.Actions.ScriptCallback).IsEqualTo("onTell");
    }

    /// <summary>
    /// The script field suggests the callbacks the configuration already names — from triggers, aliases,
    /// timers and macros alike — because a name is only useful if the user's own Lua defines it, and
    /// what they have already written is the only honest evidence of that. A rewrite and a response have
    /// no such vocabulary and so offer nothing for ↑↓ to step through.
    /// </summary>
    [Test]
    public async Task TheScriptFieldSuggestsTheCallbacksTheConfigurationAlreadyNames()
    {
        var sets = Sets();

        var script = Field(sets, 1, TriggersScreenRenderer.ScriptField);
        await Assert.That(script.Choices).IsEquivalentTo(new[] { "onTell", "onGreet", "onTick" });

        // Suggestions, not a closed list: a callback nothing calls yet is exactly what a new rule names.
        await Assert.That(script.Validate("onSomethingNew")).IsNull();

        await Assert.That(Field(sets, 0, TriggersScreenRenderer.RewriteField).Choices).IsNull();
        await Assert.That(Field(sets, 0, TriggersScreenRenderer.ResponseField).Choices).IsNull();
    }

    /// <summary>A configuration that names no callbacks has nothing to suggest, and must not pretend to.</summary>
    [Test]
    public async Task TheScriptFieldOffersNoChoicesWhenNothingNamesACallback()
    {
        var sets = new List<TriggerSet>
        {
            new()
            {
                Name = "Comms",
                Triggers = new List<Trigger> { new() { Name = "Tell", Pattern = "tells you" } },
            },
        };

        await Assert.That(Field(sets, 0, TriggersScreenRenderer.ScriptField).Choices).IsNull();
    }

    // ---- attributes ------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="TextAttributes"/> is a <c>[Flags]</c> enum — several independent booleans — so the
    /// field is a multi-select read and written as a list of names, not a one-of-N choice. It carries no
    /// <see cref="ScreenField.Choices"/> on purpose: ↑↓ step one-of-N, and the <c>↑↓ choose</c> header
    /// hint is derived from that property, so a cycling field here would advertise keys that cannot mean
    /// anything.
    /// </summary>
    [Test]
    public async Task TheAttributesFieldIsAMultiSelectAndNotACyclingChoice()
    {
        var sets = Sets();
        var field = Field(sets, 0, TriggersScreenRenderer.AttributesField);

        await Assert.That(field.Get()).IsEqualTo("bold");
        await Assert.That(field.Choices).IsNull();
        await Assert.That(field.Cycle("bold", 1)).IsNull();
    }

    [Test]
    public async Task SeveralAttributesAreSetAtOnce_AndNoneClearsThem()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var edits = new ScreenEdits();

        await Assert.That(edits.Apply(Field(sets, 0, TriggersScreenRenderer.AttributesField), "bold underline"))
            .IsNull();
        await Assert.That(trigger.Actions.AddAttributes)
            .IsEqualTo(TextAttributes.Bold | TextAttributes.Underline);
        await Assert.That(Field(sets, 0, TriggersScreenRenderer.AttributesField).Get()).IsEqualTo("bold underline");

        edits.Apply(Field(sets, 0, TriggersScreenRenderer.AttributesField), "none");
        await Assert.That(trigger.Actions.AddAttributes).IsEqualTo(TextAttributes.None);
        await Assert.That(Field(sets, 0, TriggersScreenRenderer.AttributesField).Get()).IsEqualTo("none");

        edits.Revert();
        await Assert.That(trigger.Actions.AddAttributes).IsEqualTo(TextAttributes.None); // kept as typed
    }

    [Test]
    public async Task AnAttributeNameTheEnumDoesNotHaveIsRefusedAndWritesNothing()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var field = Field(sets, 0, TriggersScreenRenderer.AttributesField);

        await Assert.That(field.Validate("bold sparkly")).IsNotNull();
        await Assert.That(new ScreenEdits().Apply(field, "sparkly")).IsNotNull();
        await Assert.That(trigger.Actions.AddAttributes).IsEqualTo(TextAttributes.Bold);

        // Case and separator liberal: the legend prints them lower-case and space-separated, but nobody
        // should be refused for typing them the way they read the enum.
        await Assert.That(field.Validate("Bold, Underline | italic")).IsNull();
    }

    /// <summary>
    /// The legend is the "row of checkboxes" this setting really is. It has to name every attribute — it
    /// is the only place the legal words appear — and it has to follow the *buffer* while the field is
    /// open, the way F2's route radios do, or typing would look inert until ⏎.
    /// </summary>
    [Test]
    public async Task TheAttributeLegendNamesEveryAttributeAndFollowsTheBuffer()
    {
        var sets = Sets();

        var resting = TriggersScreenRenderer.EditorColumn(sets, 0, Targets);
        foreach (var name in new[]
        {
            "bold", "faint", "italic", "underline", "blink", "reverse", "conceal", "strikethrough",
        })
        {
            await Assert.That(resting.Any(l => l.Contains(name, StringComparison.Ordinal)))
                .IsTrue()
                .Because(name + " is missing from the legend");
        }

        await Assert.That(resting.Any(l => l.Contains("✓bold", StringComparison.Ordinal))).IsTrue();
        await Assert.That(resting.Any(l => l.Contains("✓italic", StringComparison.Ordinal))).IsFalse();

        // Mid-edit the legend reads the buffer, not config: "italic" lights before anything is written.
        var typing = new ScreenFocus(
            0, 0, new ScreenFieldEdit(TriggersScreenRenderer.AttributesField, "italic", 6, null));
        var open = TriggersScreenRenderer.EditorColumn(sets, 0, Targets, typing);

        await Assert.That(open.Any(l => l.Contains("✓italic", StringComparison.Ordinal))).IsTrue();
        await Assert.That(open.Any(l => l.Contains("✓bold", StringComparison.Ordinal))).IsFalse();
        await Assert.That(sets[0].Triggers[0].Actions.AddAttributes).IsEqualTo(TextAttributes.Bold);
    }

    /// <summary>
    /// The section caption reports whether a matching line is restyled at all. It used to read the two
    /// colours only, so a rule that merely bolded its match was captioned "left alone" while visibly
    /// bolding every line it matched — the engine restyles on a colour *or* an attribute.
    /// </summary>
    [Test]
    public async Task TheHighlightCaptionCountsAttributesAndNotOnlyColours()
    {
        var sets = Sets();

        var bolded = TriggersScreenRenderer.EditorColumn(sets, 0, Targets)
            .Single(l => l.Contains("highlight", StringComparison.Ordinal));
        await Assert.That(bolded).Contains("restyled");
        await Assert.That(bolded).DoesNotContain("left alone");

        var plain = TriggersScreenRenderer.EditorColumn(sets, 1, Targets)
            .Single(l => l.Contains("highlight", StringComparison.Ordinal));
        await Assert.That(plain).Contains("left alone");
    }

    // ---- how the pane draws them -----------------------------------------------------------------

    /// <summary>
    /// An unset action reads <c>(off)</c> rather than as an empty gap, and keeps its field well: the row
    /// is still a place a value goes, and the well is how these screens say so (see
    /// <see cref="ScreenChrome.ReadOnly"/> for the other half of that rule).
    /// </summary>
    [Test]
    public async Task AnUnsetActionSaysSoInItsOwnWell()
    {
        var sets = Sets();
        var well = "on " + ScreenPalette.FieldBg;

        var configured = TriggersScreenRenderer.EditorColumn(sets, 0, Targets)
            .Single(l => l.Contains("respond", StringComparison.Ordinal));
        await Assert.That(configured).Contains("page $1=busy");
        await Assert.That(configured).Contains(well);

        var unset = TriggersScreenRenderer.EditorColumn(sets, 1, Targets)
            .Single(l => l.Contains("respond", StringComparison.Ordinal));
        await Assert.That(unset).Contains("(off)");
        await Assert.That(unset).Contains(well);
    }

    /// <summary>
    /// Every new field's buffer is drawn where that field's value already lives, and nowhere else — the
    /// same promise <see cref="ScreenFieldRenderingTests"/> makes for the name and the pattern.
    /// </summary>
    [Test]
    public async Task EachNewFieldDrawsItsBufferOnItsOwnRow()
    {
        var caret = $"[{ScreenPalette.Ink} on {ScreenPalette.Accent}]";
        var sets = Sets();

        foreach (var (ordinal, label, typed) in new[]
        {
            (TriggersScreenRenderer.AttributesField, "attrs", "underline"),
            (TriggersScreenRenderer.RewriteField, "rewrite", "«$1»"),
            (TriggersScreenRenderer.ResponseField, "respond", "say ok"),
            (TriggersScreenRenderer.ScriptField, "script", "onWhisper"),
        })
        {
            var lines = TriggersScreenRenderer.EditorColumn(
                sets, 0, Targets, new ScreenFocus(0, 0, new ScreenFieldEdit(ordinal, typed, typed.Length, null)));

            var carried = lines.Where(l => l.Contains(caret, StringComparison.Ordinal)).ToList();
            await Assert.That(carried).HasSingleItem().Because(label);
            await Assert.That(carried[0]).Contains(label);
        }
    }

    /// <summary>
    /// The rule list's flag strip has to name every action, or the list is a summary that quietly omits
    /// half of what a rule does. Attributes fold into <c>H</c> because they are highlighting — the engine
    /// restyles through one path — and a rewrite gets its own mark.
    /// </summary>
    [Test]
    public async Task TheRuleListFlagsNameTheRewriteResponseScriptAndAttributes()
    {
        var sets = Sets();
        var rules = TriggersScreenRenderer.RulesColumn(sets, 0);

        var row = rules.FindIndex(l => l.Contains("[bold]Tell[/]", StringComparison.Ordinal));
        var flags = rules[row + 1];

        await Assert.That(flags).Contains('H');   // AddAttributes alone, with no colour set
        await Assert.That(flags).Contains('✎');   // rewrite
        await Assert.That(flags).Contains('R');   // response
        await Assert.That(flags).Contains('ƒ');   // script

        var quiet = rules.FindIndex(l => l.Contains("[bold]Spam[/]", StringComparison.Ordinal));
        await Assert.That(rules[quiet + 1]).Contains('—');
    }

    // ---- case sensitivity ------------------------------------------------------------------------

    /// <summary>
    /// The checkbox is appended after gag and stop-processing, so the two rows the screen already
    /// navigated by keep their ordinals; and flipping it must drop the compiled matcher, because the
    /// casing is baked into that regex's options. This is the F2 half of the guarantee
    /// <c>ConfigurationTests.AliasCaseSensitivity_IsSettableAndDropsTheCachedRegex</c> makes for F3.
    /// </summary>
    [Test]
    public async Task TheCaseSensitiveCheckboxWritesTheRuleAndInvalidatesItsMatcher()
    {
        var sets = Sets();
        var trigger = sets[0].Triggers[0];
        var edits = new ScreenEdits();

        await Assert.That(trigger.Regex.IsMatch("SOMEONE TELLS YOU hi")).IsTrue();

        var toggle = TriggersScreenRenderer.Model(sets, 0, Targets).ToggleAt(1, 2)!.Value;
        edits.Apply(toggle);

        await Assert.That(trigger.CaseSensitive).IsTrue();
        await Assert.That(trigger.Regex.IsMatch("SOMEONE TELLS YOU hi")).IsFalse();
        await Assert.That(trigger.Regex.IsMatch("someone tells you hi")).IsTrue();

        // Kept, matcher and all — the checkbox is committed by the Space that pressed it. What this test
        // is really about is that the compiled regex follows the flag, and it does in both directions.
        edits.Revert();

        await Assert.That(trigger.CaseSensitive).IsTrue();
        await Assert.That(trigger.Regex.IsMatch("SOMEONE TELLS YOU hi")).IsFalse();
    }

    /// <summary>The same thing through the real keyboard: Space on the editor pane's third row.</summary>
    [Test]
    public async Task SpaceOnTheThirdEditorRowFlipsCaseSensitivity()
    {
        var sets = Sets();
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.SelectionIn(0), Targets));

        session.Handle(Key(ConsoleKey.Tab));       // into the editor pane
        session.Handle(Key(ConsoleKey.DownArrow)); // gag → stop processing
        session.Handle(Key(ConsoleKey.DownArrow)); // stop processing → case sensitive
        session.Handle(Key(ConsoleKey.Spacebar));

        await Assert.That(sets[0].Triggers[0].CaseSensitive).IsTrue();
        await Assert.That(sets[0].Triggers[0].Actions.Gag).IsFalse();
        await Assert.That(sets[0].Triggers[0].StopProcessing).IsFalse();

        var editor = TriggersScreenRenderer.EditorColumn(sets, 0, Targets, session.Focus());
        await Assert.That(editor.Any(l => l.Contains("[[x]]") && l.Contains("case sensitive"))).IsTrue();
    }
}
