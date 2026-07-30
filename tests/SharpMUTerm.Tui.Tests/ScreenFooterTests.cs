using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The hint-honesty rule, applied to the footer action bar. <see cref="ScreenCursorTests"/> pins it
/// for the header: a screen may not advertise a key that does something else. The footer makes the
/// louder claim of the two — it is a pair of filled chips, not grey hint text — and it used to keep
/// saying <c>[[⏎]] Save</c> while a field edit was open, where ⏎ commits the field and Esc abandons
/// its buffer. Neither closes the screen at that moment, so both chips named the wrong action.
/// <para>
/// The rule is asserted for every screen and in both directions, so a screen cannot pass by never
/// naming a key at all, and a seventh screen added later cannot quietly opt out.
/// </para>
/// </summary>
public class ScreenFooterTests
{
    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger> { new() { Name = "Tell", Pattern = "tells you" } },
            Aliases = new List<Alias> { new() { Name = "k", Pattern = "^k$", Substitution = "kill" } },
            Macros = new List<Macro> { new() { Name = "look", Key = "Num5", Command = "look" } },
            Timers = new List<TimerDefinition> { new() { Name = "ping", IntervalSeconds = 30, Command = "look" } },
        },
    };

    private static List<WorldDefinition> Worlds() => new()
    {
        new WorldDefinition
        {
            Name = "Aardwolf",
            Host = "aardmud.org",
            Characters = new List<CharacterDefinition> { new() { Name = "Kaz" } },
        },
    };

    /// <summary>A cursor with a field open on it — what every screen's footer has to answer for.</summary>
    private static ScreenFocus Editing => new(0, 0, new ScreenFieldEdit(0, "aardmud.org", 11, null));

    /// <summary>Every screen's footer, navigating and mid-edit, so the rule is asserted per screen.</summary>
    private static List<(string Name, string Navigating, string Mid)> Footers()
    {
        var sets = Sets();
        var worlds = Worlds();
        var macros = sets[0].Macros;
        var options = OptionsScreenRenderer.InputScreen();
        var accent = WorldsScreenRenderer.AccentFor(worlds, 0);

        return new List<(string, string, string)>
        {
            ("F2 triggers",
                TriggersScreenRenderer.FooterLine(sets, 0, 80),
                TriggersScreenRenderer.FooterLine(sets, 0, 80, Editing)),
            ("F3 aliases",
                AliasesScreenRenderer.FooterLine(sets, 0, 80),
                AliasesScreenRenderer.FooterLine(sets, 0, 80, Editing)),
            ("F4 keypad",
                KeypadScreenRenderer.FooterLine(macros, 80),
                KeypadScreenRenderer.FooterLine(macros, 80, Editing)),
            ("F5 worlds",
                WorldsScreenRenderer.FooterLine(worlds, 0, 0, accent, 80),
                WorldsScreenRenderer.FooterLine(worlds, 0, 0, accent, 80, Editing)),
            ("F6 timers",
                TimersScreenRenderer.FooterLine(sets, 0, 80),
                TimersScreenRenderer.FooterLine(sets, 0, 80, Editing)),
            ("F7/F8 options",
                OptionsScreenRenderer.FooterLine(options.Rows, 80),
                OptionsScreenRenderer.FooterLine(options.Rows, 80, Editing)),
        };
    }

    /// <summary>
    /// While the screen is navigating, Esc <b>closes</b> — and the footer says so. It said <c>Cancel</c>
    /// until the key stopped cancelling: closing keeps every committed edit now, and a chip promising to
    /// cancel is how a user comes to believe that leaving a screen is safe to do with unsaved work and
    /// that F5 will hand it back. The ⏎ chip is checked separately, because what ⏎ does depends on the
    /// row.
    /// </summary>
    [Test]
    public async Task FooterActions_SayCloseWhileTheScreenIsNavigating()
    {
        foreach (var (name, navigating, _) in Footers())
        {
            await Assert.That(navigating).Contains(ScreenChrome.CloseAction).Because(name);
            await Assert.That(navigating).DoesNotContain(ScreenChrome.CommitAction).Because(name);
            await Assert.That(navigating).DoesNotContain(ScreenChrome.RevertAction).Because(name);
            await Assert.That(navigating).DoesNotContain("Cancel").Because(name);
            await Assert.That(navigating).DoesNotContain("Save").Because(name);
        }
    }

    /// <summary>
    /// And the ⏎ chip names what ⏎ would do <em>on the row the cursor is on</em>, which is the same
    /// honesty rule the header's hints follow. It used to read <c>[[⏎]] Save</c> on every row of every
    /// screen; on a world's row ⏎ opens an editor and on <c>[[+ world]]</c> it adds a world, so the chip
    /// was wrong nearly everywhere it was drawn — and wrong in the one direction that matters, since
    /// "Save" is a promise about the user's work.
    /// </summary>
    [Test]
    public async Task FooterActions_NameWhatEnterDoesOnTheFocusedRow()
    {
        var worlds = Worlds();
        var accent = WorldsScreenRenderer.AccentFor(worlds, 0);

        string Footer(ScreenFocus focus) => WorldsScreenRenderer.FooterLine(worlds, 0, 0, accent, 80, focus);

        // A world's row: ⏎ opens its name.
        await Assert.That(Footer(new ScreenFocus(0, 0, Enter: ScreenEnter.Edit)))
            .Contains(ScreenChrome.EditAction);

        // [+ world]: ⏎ runs it.
        await Assert.That(Footer(new ScreenFocus(0, 2, Enter: ScreenEnter.Add)))
            .Contains(ScreenChrome.AddAction);

        // A security checkbox: nothing to open, so ⏎ leaves — and says "Done", not "Save", because
        // every committed value is already on disk.
        await Assert.That(Footer(new ScreenFocus(3, 0)))
            .Contains(ScreenChrome.DoneAction);
    }

    /// <summary>
    /// The bug this rule exists for: mid-edit, ⏎ commits the open field and Esc throws its buffer away.
    /// A footer still offering Save and Cancel is naming two keys that do something else, which is the
    /// same lie the header is already forbidden to tell.
    /// </summary>
    [Test]
    public async Task FooterActions_NameCommitAndRevertWhileAFieldEditIsOpen()
    {
        foreach (var (name, _, mid) in Footers())
        {
            await Assert.That(mid).Contains(ScreenChrome.CommitAction).Because(name);
            await Assert.That(mid).Contains(ScreenChrome.RevertAction).Because(name);
            await Assert.That(mid).DoesNotContain(ScreenChrome.CloseAction).Because(name);
            await Assert.That(mid).DoesNotContain(ScreenChrome.DoneAction).Because(name);
        }
    }

    /// <summary>
    /// The header and the footer are driven by the same <see cref="ScreenFocus"/>, so they can never
    /// disagree about whether an edit is open — which is exactly how they came to disagree before.
    /// </summary>
    [Test]
    public async Task TheHeaderAndTheFooterAgreeAboutWhetherAnEditIsOpen()
    {
        var worlds = Worlds();
        var sets = Sets();
        var model = WorldsScreenRenderer.Model(worlds, sets, 0, 0);
        var accent = WorldsScreenRenderer.AccentFor(worlds, 0);

        foreach (var focus in new ScreenFocus?[] { null, new ScreenFocus(0, 0), Editing })
        {
            var header = WorldsScreenRenderer.HeaderLine(80, model, focus);
            var footer = WorldsScreenRenderer.FooterLine(worlds, 0, 0, accent, 80, focus);

            var headerSaysEditing = header.Contains(ScreenChrome.EditingHints, StringComparison.Ordinal);
            var footerSaysEditing = footer.Contains(ScreenChrome.CommitAction, StringComparison.Ordinal);

            await Assert.That(headerSaysEditing).IsEqualTo(footerSaysEditing);
            await Assert.That(headerSaysEditing).IsEqualTo(focus?.Edit is not null);
        }
    }

    /// <summary>
    /// A footer with a screen accent still swaps its verbs. F5 tints its ⏎ chip with the selected
    /// world's colour, and the colour is the one thing that must *not* change what the chip says.
    /// </summary>
    [Test]
    public async Task AnAccentedFooterSwapsItsVerbsToo()
    {
        var accented = ScreenChrome.Actions("#ff00ff", Editing);

        await Assert.That(accented).Contains("#ff00ff");
        await Assert.That(accented).Contains(ScreenChrome.CommitAction);
        await Assert.That(accented).DoesNotContain(ScreenChrome.DoneAction);
    }

    /// <summary>
    /// ↑↓ is offered only by a field that has choices to step through. It is the same rule as ⇥, which
    /// is offered only when the row holds another field: a hint for a key that would do nothing is the
    /// bug this file is about, one keystroke smaller.
    /// <para>
    /// The rule now bites one level finer, because ↑↓ walk the list the dropdown is *showing*: a buffer
    /// that has narrowed that list to nothing leaves the keys with nowhere to go, and the hint goes with
    /// them.
    /// </para>
    /// </summary>
    [Test]
    public async Task EditingHints_OfferTheChoiceKeysOnlyForAFieldThatHasChoices()
    {
        var routes = new[] { "main", "Chat", "pages" };
        var free = ScreenChrome.Hints(
            ScreenChrome.ListHints, "F5", true, new ScreenFocus(0, 0, new ScreenFieldEdit(0, "x", 1, null)));
        var choices = ScreenChrome.Hints(
            ScreenChrome.ListHints,
            "F5",
            true,
            new ScreenFocus(0, 0, new ScreenFieldEdit(0, "main", 4, null, Choices: routes)));
        var narrowed = ScreenChrome.Hints(
            ScreenChrome.ListHints,
            "F5",
            true,
            new ScreenFocus(0, 0, new ScreenFieldEdit(0, "pa", 2, null, Choices: routes)));
        var matchedNothing = ScreenChrome.Hints(
            ScreenChrome.ListHints,
            "F5",
            true,
            new ScreenFocus(0, 0, new ScreenFieldEdit(0, "combat", 6, null, Choices: routes)));

        await Assert.That(free).DoesNotContain(ScreenChrome.ChoiceHint);
        await Assert.That(choices).Contains(ScreenChrome.ChoiceHint);
        await Assert.That(narrowed).Contains(ScreenChrome.ChoiceHint);
        await Assert.That(matchedNothing).DoesNotContain(ScreenChrome.ChoiceHint);
    }
}
