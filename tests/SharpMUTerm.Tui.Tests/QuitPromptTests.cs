using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The ⌃Q confirmation's rules and its wording, without a terminal. Everything a modal is actually
/// judged on — which key means yes, which key the default stands on, and whether the question is worth
/// asking — lives in <see cref="QuitPrompt"/> precisely so it can be read back here; the overlay around
/// it is framework calls and nothing else.
/// </summary>
public class QuitPromptTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Letter(char c, ConsoleKey key) => new(c, key, false, false, false);

    private static ConsoleKeyInfo Chord(ConsoleKey key, bool ctrl = false, bool alt = false) =>
        new('\0', key, false, alt, ctrl);

    private static QuitFacts Busy() => new(
        new[] { "Aetherfall", "Grapevine" },
        2,
        new[] { "main", "Chat" });

    // --- what a keystroke means ------------------------------------------------

    /// <summary>y and n answer outright, whichever button happens to be pointed at.</summary>
    [Test]
    [Arguments('y', ConsoleKey.Y, true)]
    [Arguments('n', ConsoleKey.N, false)]
    public async Task TheLetterAnswersTheQuestion(char c, ConsoleKey key, bool quits)
    {
        var expected = quits ? QuitAction.Quit : QuitAction.Stay;

        await Assert.That(QuitPrompt.Interpret(Letter(c, key), QuitChoice.Stay).Action).IsEqualTo(expected);
        await Assert.That(QuitPrompt.Interpret(Letter(c, key), QuitChoice.Quit).Action).IsEqualTo(expected);
    }

    /// <summary>A terminal that reports no <see cref="ConsoleKey"/> for a letter still reports the char.</summary>
    [Test]
    public async Task ALetterWithNoConsoleKeyStillAnswers()
    {
        var decision = QuitPrompt.Interpret(Letter('y', ConsoleKey.NoName), QuitChoice.Stay);

        await Assert.That(decision.Action).IsEqualTo(QuitAction.Quit);
    }

    /// <summary>
    /// The default: ⏎ on a freshly opened prompt stays. A confirmation whose ⏎ quits is defeated by any
    /// second stray keystroke, which is the same accident it was put there to catch.
    /// </summary>
    [Test]
    public async Task EnterOnTheDefaultStays()
    {
        var decision = QuitPrompt.Interpret(Key(ConsoleKey.Enter), QuitChoice.Stay);

        await Assert.That(decision.Action).IsEqualTo(QuitAction.Stay);
    }

    /// <summary>And ⏎ once the pointer has been moved onto Quit does quit — the button is not decoration.</summary>
    [Test]
    public async Task EnterOnQuitQuits()
    {
        var moved = QuitPrompt.Interpret(Key(ConsoleKey.LeftArrow), QuitChoice.Stay);
        await Assert.That(moved.Action).IsEqualTo(QuitAction.Redraw);
        await Assert.That(moved.Selection).IsEqualTo(QuitChoice.Quit);

        await Assert.That(QuitPrompt.Interpret(Key(ConsoleKey.Enter), moved.Selection).Action)
            .IsEqualTo(QuitAction.Quit);
    }

    /// <summary>
    /// ←/→ address the buttons by position and ⇥ walks between them. These are bound only inside the
    /// modal: unprefixed, ← and → belong to the command line's caret and to the ⌃B tab reorder.
    /// </summary>
    [Test]
    [Arguments(ConsoleKey.LeftArrow, false, true)]
    [Arguments(ConsoleKey.LeftArrow, true, true)]
    [Arguments(ConsoleKey.RightArrow, true, false)]
    [Arguments(ConsoleKey.Tab, false, true)]
    [Arguments(ConsoleKey.Tab, true, false)]
    public async Task TheArrowsAndTabPickAButton(ConsoleKey key, bool fromQuit, bool toQuit)
    {
        var decision = QuitPrompt.Interpret(Key(key), fromQuit ? QuitChoice.Quit : QuitChoice.Stay);

        await Assert.That(decision.Action).IsEqualTo(QuitAction.Redraw);
        await Assert.That(decision.Selection).IsEqualTo(toQuit ? QuitChoice.Quit : QuitChoice.Stay);
    }

    /// <summary>Esc is the way out of every modal state in this client, and it means no here as well.</summary>
    [Test]
    public async Task EscapeStays()
    {
        await Assert.That(QuitPrompt.Interpret(Key(ConsoleKey.Escape), QuitChoice.Quit).Action)
            .IsEqualTo(QuitAction.Stay);
    }

    /// <summary>
    /// A second ⌃Q dismisses the question rather than confirming it. Every surface in this client closes
    /// on the chord that opened it, and the other reading would make ⌃Q ⌃Q — which a held key produces
    /// on its own — an unguarded quit.
    /// </summary>
    [Test]
    public async Task ASecondCtrlQStays()
    {
        var decision = QuitPrompt.Interpret(Chord(ConsoleKey.Q, ctrl: true), QuitChoice.Stay);

        await Assert.That(decision.Action).IsEqualTo(QuitAction.Stay);
    }

    /// <summary>Everything else is swallowed, pointer untouched — including a modified y.</summary>
    [Test]
    [Arguments(ConsoleKey.Spacebar, false, false)]
    [Arguments(ConsoleKey.UpArrow, false, false)]
    [Arguments(ConsoleKey.Y, true, false)]
    [Arguments(ConsoleKey.Y, false, true)]
    [Arguments(ConsoleKey.W, true, false)]
    public async Task EverythingElseIsSwallowed(ConsoleKey key, bool ctrl, bool alt)
    {
        var decision = QuitPrompt.Interpret(Chord(key, ctrl, alt), QuitChoice.Stay);

        await Assert.That(decision.Action).IsEqualTo(QuitAction.None);
        await Assert.That(decision.Selection).IsEqualTo(QuitChoice.Stay);
    }

    // --- what the question says ------------------------------------------------

    /// <summary>
    /// With nothing connected and nothing typed there is nothing to weigh, and the prompt says so rather
    /// than listing an empty inventory — that sentence is the answer to its own question.
    /// </summary>
    [Test]
    public async Task WithNothingAtStakeItSaysSo()
    {
        await Assert.That(QuitPrompt.Consequences(QuitFacts.Nothing)).IsEmpty();

        var rendered = string.Join("\n", QuitPrompt.Render(QuitFacts.Nothing, QuitChoice.Stay));
        await Assert.That(rendered).Contains("Nothing is connected and nothing is unsent.");
    }

    /// <summary>
    /// The case the modal exists for: it names the connections it would drop and the drafts it would
    /// throw away, so the question can be answered instead of performed.
    /// </summary>
    [Test]
    public async Task ItNamesTheConnectionsAndTheDrafts()
    {
        var consequences = QuitPrompt.Consequences(Busy());

        await Assert.That(consequences[0]).IsEqualTo("2 worlds connected — Aetherfall, Grapevine");
        await Assert.That(consequences[1]).IsEqualTo("2 unsent drafts — main, Chat");
        await Assert.That(consequences).Count().IsEqualTo(2);
    }

    /// <summary>One of each is singular. A prompt reading "1 worlds connected" is a prompt nobody proofread.</summary>
    [Test]
    public async Task OneOfEachReadsAsOne()
    {
        var facts = new QuitFacts(new[] { "Aetherfall" }, 1, new[] { "main" });

        await Assert.That(QuitPrompt.Consequences(facts)).IsEquivalentTo(new[]
        {
            "1 world connected — Aetherfall",
            "1 unsent draft — main",
        });
    }

    /// <summary>
    /// Past a few, names stop identifying anything and only cost width — so they are counted instead.
    /// </summary>
    [Test]
    public async Task ALongListIsCappedAndCounted()
    {
        var facts = QuitFacts.Nothing with { ConnectedWorlds = new[] { "A", "B", "C", "D", "E" } };

        await Assert.That(QuitPrompt.Consequences(facts)[0]).IsEqualTo("5 worlds connected — A, B, C +2 more");
    }

    /// <summary>
    /// An open settings screen is no longer a consequence of quitting at all, in either direction. It
    /// used to contribute "Timers is open — 2 unsaved edits", which was true only while closing a screen
    /// could throw its edits away; a screen now writes each change out as it commits it
    /// (<see cref="ScreenEdits"/>), so there is never an unsaved edit for the prompt to warn about and
    /// the fact was removed rather than left permanently zero.
    /// </summary>
    [Test]
    public async Task AnOpenScreenIsNotAConsequence()
    {
        await Assert.That(QuitPrompt.Consequences(QuitFacts.Nothing)).IsEmpty();
        await Assert.That(typeof(QuitFacts).GetProperty("UnsavedEdits")).IsNull();
    }

    /// <summary>
    /// The default is drawn, not implied: the Stay chip carries the accent block and Quit does not, and
    /// the footer names the key ⏎ is standing on. Both flip together when the pointer moves, or the
    /// surface would be telling the user two different things.
    /// </summary>
    [Test]
    public async Task TheDefaultIsVisiblyStay()
    {
        var resting = string.Join("\n", QuitPrompt.Render(Busy(), QuitChoice.Stay));
        await Assert.That(resting).Contains($"[{ScreenPalette.Ink} on {ScreenPalette.Accent}] ▸ Stay  n [/]");
        await Assert.That(resting).DoesNotContain($"[{ScreenPalette.Ink} on {ScreenPalette.Accent}] ▸ Quit  y [/]");
        await Assert.That(resting).Contains("⏎ stay");

        var moved = string.Join("\n", QuitPrompt.Render(Busy(), QuitChoice.Quit));
        await Assert.That(moved).Contains($"[{ScreenPalette.Ink} on {ScreenPalette.Accent}] ▸ Quit  y [/]");
        await Assert.That(moved).Contains("⏎ quit");
    }

    /// <summary>Both buttons name the letter that runs them, so y/n is readable off the surface itself.</summary>
    [Test]
    public async Task TheButtonsCarryTheirKeys()
    {
        var rendered = string.Join("\n", QuitPrompt.Render(QuitFacts.Nothing, QuitChoice.Stay));

        await Assert.That(rendered).Contains("Quit  y");
        await Assert.That(rendered).Contains("Stay  n");
        await Assert.That(rendered).Contains("⌃Q again stays");
    }
}
