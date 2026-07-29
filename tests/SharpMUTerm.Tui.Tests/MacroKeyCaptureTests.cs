using System.Text.RegularExpressions;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What F4 now knows about the keyboard, and how a binding is rebound.
/// <para>
/// Two rules are asserted here and they are the same rule twice. A screen may not draw a row that
/// quietly does nothing — so every binding carries <see cref="MacroKeys.Verdict"/>, and the ones that
/// cannot fire say so. And a screen may not <em>create</em> such a row — so the key capture refuses a
/// chord this host cannot deliver and a chord another binding already holds, at the moment the key is
/// pressed rather than the first time the user wonders why nothing happened.
/// </para>
/// </summary>
public class MacroKeyCaptureTests
{
    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Macros = new List<Macro>
            {
                new() { Name = "Look", Key = "Num5", Command = "look" },
                new() { Name = "Score", Key = "Ctrl+F1", Command = "score" },
            },
        },
    };

    private static SettingsSession Session(IReadOnlyList<TriggerSet> sets) =>
        new(selection => KeypadScreenRenderer.Model(
            sets.SelectMany(s => s.Macros).ToList(), sets, selection.SelectionIn(0)));

    private static ConsoleKeyInfo Chord(ConsoleKey key, bool ctrl = false, bool alt = false, bool shift = false) =>
        new('\0', key, shift, alt, ctrl);

    /// <summary>Opens the binding row's key field: ⏎ opens the name, then ⇥ twice reaches the key.</summary>
    private static void ArmCapture(SettingsSession session)
    {
        session.Handle(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        session.Handle(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
        session.Handle(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
    }

    // ---- what the host can actually deliver -------------------------------------------------------

    /// <summary>
    /// The verdicts, each one a fact about SharpConsoleUI's input parser, this app's own shortcuts, or
    /// the prompt — read out of the parser at v2.5.14, not assumed. The numpad row is the one the audit
    /// turned up: the framework sends no DECKPAM and decodes no application-keypad SS3, and
    /// <c>ConsoleKey.NumPad0</c> appears nowhere in it, so a numpad chord is not merely unbound here —
    /// it cannot arrive.
    /// </summary>
    [Test]
    [Arguments("Ctrl+F1", nameof(MacroKeyDelivery.Fires))]
    [Arguments("F1", nameof(MacroKeyDelivery.Fires))]
    [Arguments("F12", nameof(MacroKeyDelivery.Fires))]
    [Arguments("Shift+F3", nameof(MacroKeyDelivery.Fires))]
    [Arguments("Ctrl+K", nameof(MacroKeyDelivery.Fires))]
    [Arguments("Alt+K", nameof(MacroKeyDelivery.Fires))]
    [Arguments("Alt+5", nameof(MacroKeyDelivery.Fires))]
    [Arguments("Ctrl+Up", nameof(MacroKeyDelivery.Fires))]
    [Arguments("Num5", nameof(MacroKeyDelivery.NeverArrives))]
    [Arguments("Num0", nameof(MacroKeyDelivery.NeverArrives))]
    [Arguments("Ctrl+Alt+K", nameof(MacroKeyDelivery.NeverArrives))]
    [Arguments("Ctrl+Shift+K", nameof(MacroKeyDelivery.NeverArrives))]
    [Arguments("Ctrl+I", nameof(MacroKeyDelivery.NeverArrives))]
    [Arguments("Ctrl+M", nameof(MacroKeyDelivery.NeverArrives))]
    [Arguments("Ctrl+5", nameof(MacroKeyDelivery.NeverArrives))]
    [Arguments("Alt+O", nameof(MacroKeyDelivery.NeverArrives))]
    [Arguments("Enter", nameof(MacroKeyDelivery.NeverArrives))]
    [Arguments("F4", nameof(MacroKeyDelivery.Taken))]
    [Arguments("F9", nameof(MacroKeyDelivery.Taken))]
    [Arguments("Ctrl+Q", nameof(MacroKeyDelivery.Taken))]
    [Arguments("Ctrl+P", nameof(MacroKeyDelivery.Taken))]
    [Arguments("K", nameof(MacroKeyDelivery.Taken))]
    [Arguments("Up", nameof(MacroKeyDelivery.Taken))]
    public async Task TheVerdictSaysWhatWillHappenToAChord(string descriptor, string expected)
    {
        var verdict = MacroKeys.Verdict(descriptor);

        await Assert.That(verdict.Delivery.ToString()).IsEqualTo(expected).Because(descriptor);
        await Assert.That(verdict.Reason.Length > 0).IsEqualTo(expected != nameof(MacroKeyDelivery.Fires))
            .Because(descriptor + " must give a reason exactly when it will not fire");
    }

    /// <summary>
    /// The dispatcher and the screen cannot disagree, by construction:
    /// <see cref="MacroKeys.Descriptor"/> is <see cref="MacroKeys.Capture"/> filtered by the same
    /// verdict F4 draws. Asserted over the whole keyboard rather than a sample, because "the list says
    /// this fires and the handler ignores it" is precisely the failure this work exists to remove.
    /// </summary>
    [Test]
    public async Task WhateverTheScreenCallsLiveIsWhatTheDispatcherActsOn()
    {
        foreach (var key in Enum.GetValues<ConsoleKey>())
        {
            foreach (var modifiers in new[]
            {
                (ctrl: false, alt: false, shift: false),
                (ctrl: true, alt: false, shift: false),
                (ctrl: false, alt: true, shift: false),
                (ctrl: false, alt: false, shift: true),
                (ctrl: true, alt: true, shift: false),
            })
            {
                var stroke = Chord(key, modifiers.ctrl, modifiers.alt, modifiers.shift);
                var captured = MacroKeys.Capture(stroke);
                var dispatched = MacroKeys.Descriptor(stroke);

                if (captured is null)
                {
                    await Assert.That(dispatched).IsNull().Because(key.ToString());
                    continue;
                }

                await Assert.That(dispatched)
                    .IsEqualTo(MacroKeys.Verdict(captured).Fires ? captured : null)
                    .Because(captured);
            }
        }
    }

    /// <summary>
    /// A binding that cannot fire is marked where it is drawn, in the muted ink behind a <c>▲</c>, and
    /// explained in the footer for the one the cursor is on. Asserted both ways round, so the mark can
    /// neither disappear from a dead row nor spread onto a live one.
    /// <para>
    /// The <em>reason</em> is deliberately not on the row: the reasons differ, they are longer than the
    /// column has cells, and repeated down a list they stop being read. The caption counts them instead,
    /// which is the one thing neither the row nor the footer can say.
    /// </para>
    /// </summary>
    [Test]
    public async Task ABindingThatCannotFireIsMarkedOnItsRowAndExplainedInTheFooter()
    {
        var macros = Sets()[0].Macros;
        var rows = KeypadScreenRenderer.HotkeysColumn(macros);

        var dead = rows.Single(l => l.Contains("Num5", StringComparison.Ordinal));
        await Assert.That(dead).Contains("▲");
        await Assert.That(dead).Contains(ScreenPalette.Muted);

        var live = rows.Single(l => l.Contains("Ctrl+F1", StringComparison.Ordinal));
        await Assert.That(live).DoesNotContain("▲");

        await Assert.That(rows[0]).Contains("HOTKEYS");
        await Assert.That(rows[0]).Contains("1 of 2 cannot fire");

        await Assert.That(KeypadScreenRenderer.FooterLine(macros, 120, new ScreenFocus(0, 0)))
            .Contains(MacroKeys.Verdict("Num5").Reason);
        await Assert.That(KeypadScreenRenderer.FooterLine(macros, 120, new ScreenFocus(0, 1)))
            .DoesNotContain("▲");
    }

    /// <summary>A list with nothing dead in it says nothing — a count of zero is not news.</summary>
    [Test]
    public async Task AListOfLiveBindingsCarriesNoCaveatAtAll()
    {
        var macros = new List<Macro> { new() { Name = "Score", Key = "Ctrl+F1", Command = "score" } };
        var rows = KeypadScreenRenderer.HotkeysColumn(macros);

        await Assert.That(rows[0]).DoesNotContain("▲");
        await Assert.That(rows.Any(l => l.Contains("▲", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// The numpad diagram is nine cells of keys none of which can arrive, so its caption says so — and
    /// says it in the verdict's own words, derived rather than written, so a host that could one day
    /// deliver the numpad would drop the disclaimer without anyone remembering to.
    /// </summary>
    [Test]
    public async Task TheNumpadGridSaysThatNothingReachesIt()
    {
        var caption = KeypadScreenRenderer.NumpadColumn(Sets()[0].Macros)[0];

        await Assert.That(caption).Contains("NUMPAD");
        await Assert.That(caption).Contains(MacroKeys.Verdict("Num5").Reason);
    }

    // ---- the capture mode ------------------------------------------------------------------------

    /// <summary>
    /// The key is the row's third field, appended after the name and the command so the two ordinals
    /// that were already addressed by the renderer, the tests and the snapshot scripts still mean what
    /// they meant. Opening it arms a capture rather than a buffer.
    /// </summary>
    [Test]
    public async Task TheBindingRowCarriesItsKeyAsACaptureField()
    {
        var sets = Sets();
        var model = KeypadScreenRenderer.Model(sets.SelectMany(s => s.Macros).ToList(), sets, 0);
        var row = model.RowAt(0, 0);

        await Assert.That(row.FieldCount).IsEqualTo(3);
        await Assert.That(row.FieldAt(KeypadScreenRenderer.NameField)!.Value.Get()).IsEqualTo("Look");
        await Assert.That(row.FieldAt(KeypadScreenRenderer.CommandField)!.Value.Get()).IsEqualTo("look");
        await Assert.That(row.FieldAt(KeypadScreenRenderer.KeyField)!.Value.Get()).IsEqualTo("Num5");
        await Assert.That(row.FieldAt(KeypadScreenRenderer.KeyField)!.Value.Capture).IsTrue();
        await Assert.That(row.FieldAt(KeypadScreenRenderer.NameField)!.Value.Capture).IsFalse();
    }

    /// <summary>The headline of task two: the next keystroke becomes the binding, canonically spelt.</summary>
    [Test]
    public async Task TheNextKeystrokeBecomesTheBinding()
    {
        var sets = Sets();
        var session = Session(sets);
        ArmCapture(session);

        await Assert.That(session.Focus().Edit!.Value.Capture).IsTrue();

        session.Handle(Chord(ConsoleKey.F7, ctrl: true, shift: true));

        await Assert.That(sets[0].Macros[0].Key).IsEqualTo("Ctrl+Shift+F7");
        await Assert.That(session.IsEditing).IsFalse();
    }

    /// <summary>
    /// A capture writes the canonical spelling whatever the terminal reports, and the two shapes already
    /// in configurations survive a round trip through it: <c>Ctrl+F1</c> is exactly what pressing Ctrl+F1
    /// produces, and <c>Num5</c> — which no keystroke can produce — is still the spelling the numpad grid
    /// and the demo configuration use, unchanged by being read.
    /// </summary>
    [Test]
    public async Task CaptureAndStoredDescriptorsAgreeOnOneSpelling()
    {
        await Assert.That(MacroKeys.Capture(Chord(ConsoleKey.F1, ctrl: true))).IsEqualTo("Ctrl+F1");
        await Assert.That(MacroKeys.Capture(new ConsoleKeyInfo('k', ConsoleKey.K, false, true, false)))
            .IsEqualTo("Alt+K");

        await Assert.That(MacroKey.Canonicalise("Ctrl+F1")).IsEqualTo("Ctrl+F1");
        await Assert.That(MacroKey.Canonicalise("Num5")).IsEqualTo("Num5");
        await Assert.That(MacroKey.Canonicalise("shift+ctrl+f1")).IsEqualTo("Ctrl+Shift+F1");
        await Assert.That(MacroKey.Canonicalise("NumPad5")).IsEqualTo("Num5");
        await Assert.That(MacroKey.Canonicalise("esc")).IsEqualTo("Escape");
        await Assert.That(MacroKey.Canonicalise("Hyper+F1")).IsNull();
    }

    /// <summary>
    /// The trap this mode could have set, and does not. Esc is how every modal state on these screens is
    /// left, and a capture that ate it would leave a user with no key that ends the prompt — ⏎ and ⇥ are
    /// themselves chords someone might want to bind, so neither can be the way out.
    /// </summary>
    [Test]
    public async Task EscapeAlwaysLeavesACaptureAndBindsNothing()
    {
        var sets = Sets();
        var session = Session(sets);
        ArmCapture(session);

        var action = session.Handle(new ConsoleKeyInfo('', ConsoleKey.Escape, false, false, false));

        await Assert.That(action).IsEqualTo(ScreenAction.Redraw);
        await Assert.That(session.IsEditing).IsFalse();
        await Assert.That(sets[0].Macros[0].Key).IsEqualTo("Num5");
    }

    /// <summary>
    /// The duplicate. A <see cref="MacroEngine"/> resolves one macro per key, so a second binding on a
    /// key never runs — and unlike every other dead row it would have no symptom at all, since both rows
    /// would look alive. The capture refuses it, names the binding already holding the key, and stays
    /// armed, so the answer to "that one is taken" is another keystroke rather than a lost edit.
    /// </summary>
    [Test]
    public async Task AKeyAnotherBindingAlreadyHoldsIsRefusedAndNamesTheOtherBinding()
    {
        var sets = Sets();
        var session = Session(sets);
        ArmCapture(session);

        session.Handle(Chord(ConsoleKey.F1, ctrl: true)); // Score's key

        var edit = session.Focus().Edit;
        await Assert.That(edit).IsNotNull();
        await Assert.That(edit!.Value.Capture).IsTrue();
        await Assert.That(edit.Value.Error).IsEqualTo("already bound to Score");
        await Assert.That(sets[0].Macros[0].Key).IsEqualTo("Num5");

        // Still armed: the next key is still the value.
        session.Handle(Chord(ConsoleKey.F11));
        await Assert.That(sets[0].Macros[0].Key).IsEqualTo("F11");
    }

    /// <summary>
    /// Re-pressing the key a binding already has is not a duplicate of itself. It commits, unchanged,
    /// which is the only sane reading of "bind this to the key it is on".
    /// </summary>
    [Test]
    public async Task PressingTheKeyThisBindingAlreadyHasSimplyCommits()
    {
        var sets = new List<TriggerSet>
        {
            new() { Name = "Comms", Macros = new List<Macro> { new() { Name = "Score", Key = "Ctrl+F1", Command = "score" } } },
        };
        var session = Session(sets);
        ArmCapture(session);

        session.Handle(Chord(ConsoleKey.F1, ctrl: true));

        await Assert.That(session.IsEditing).IsFalse();
        await Assert.That(sets[0].Macros[0].Key).IsEqualTo("Ctrl+F1");
    }

    /// <summary>
    /// A chord this host cannot deliver is refused at the capture, in the verdict's own words. The point
    /// is that the refusal happens while the user's finger is still on the key: the alternative is a row
    /// that looks bound, is bound, and does nothing.
    /// </summary>
    [Test]
    [Arguments(ConsoleKey.NumPad5, false, false, "no numpad key ever arrives")]
    [Arguments(ConsoleKey.K, false, false, "plain keys type into the prompt")]
    [Arguments(ConsoleKey.F4, false, false, "F4 opens this screen")]
    [Arguments(ConsoleKey.Q, true, false, "Ctrl+Q quits")]
    [Arguments(ConsoleKey.I, true, false, "the terminal sends Tab instead")]
    [Arguments(ConsoleKey.O, false, true, "Alt+O is the terminal's own prefix")]
    public async Task AChordThatCouldNeverFireIsRefusedAtTheCapture(
        ConsoleKey key, bool ctrl, bool alt, string reason)
    {
        var sets = Sets();
        var session = Session(sets);
        ArmCapture(session);

        session.Handle(Chord(key, ctrl, alt));

        await Assert.That(session.Focus().Edit!.Value.Error).IsEqualTo(reason);
        await Assert.That(sets[0].Macros[0].Key).IsEqualTo("Num5");
    }

    /// <summary>
    /// The undo log covers a rebinding like any other edit: Esc on the screen puts the old key back, so
    /// a capture is no more permanent than typing into a well.
    /// </summary>
    [Test]
    public async Task CancellingTheScreenPutsTheOldKeyBack()
    {
        var sets = Sets();
        var session = Session(sets);
        ArmCapture(session);
        session.Handle(Chord(ConsoleKey.F11));
        await Assert.That(sets[0].Macros[0].Key).IsEqualTo("F11");

        session.Edits.Revert();

        await Assert.That(sets[0].Macros[0].Key).IsEqualTo("Num5");
    }

    /// <summary>
    /// The chrome while a capture is armed says exactly what is true of the keyboard at that moment: one
    /// key gets you out, everything else is the value. It may not offer ⏎ commit, ⇥ next field or
    /// <c>F4 close</c>, because all three are keys that mean something else while the prompt is up — the
    /// same rule that forbids a header claiming <c>⏎ edit</c> on a screen with nothing to edit.
    /// </summary>
    [Test]
    public async Task TheChromeOffersOnlyWhatACaptureActuallyAnswersTo()
    {
        var sets = Sets();
        var session = Session(sets);
        ArmCapture(session);
        var focus = session.Focus();

        var header = KeypadScreenRenderer.HeaderLine(
            120, KeypadScreenRenderer.Model(sets[0].Macros, sets, 0), focus);
        await Assert.That(header).Contains(ScreenChrome.CaptureHints);
        await Assert.That(header).DoesNotContain(ScreenChrome.EditingHints);
        await Assert.That(header).DoesNotContain(ScreenChrome.NextFieldHint);
        await Assert.That(Visible(header)).DoesNotContain("F4 close");

        var footer = KeypadScreenRenderer.FooterLine(sets[0].Macros, 120, focus);
        await Assert.That(footer).Contains(ScreenChrome.BindAction);
        await Assert.That(footer).DoesNotContain(ScreenChrome.CommitAction);

        // And the field itself is a prompt, not a caret over a buffer there is no way to type into.
        var row = KeypadScreenRenderer.HotkeysColumn(sets[0].Macros, focus)
            .Single(l => l.Contains(ScreenChrome.CapturePrompt, StringComparison.Ordinal));
        await Assert.That(row).Contains("look");
    }

    /// <summary>A markup line as it prints: tags stripped, escaped brackets folded back to one.</summary>
    private static string Visible(string markup)
    {
        var guarded = markup.Replace("[[", "", StringComparison.Ordinal)
            .Replace("]]", "", StringComparison.Ordinal);
        return Regex.Replace(guarded, @"\[[^\[\]]*\]", string.Empty)
            .Replace('', '[')
            .Replace('', ']');
    }
}
