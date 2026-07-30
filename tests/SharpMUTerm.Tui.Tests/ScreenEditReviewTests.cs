using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The closing deletion review, minus the window: what a keystroke means to it and what it says.
/// <see cref="SettingsCloseTests"/> pins that a screen is wired to it; these pin the rules, the same way
/// <see cref="QuitPromptTests"/> does for ⌃Q — and for the same reason, which is that the rules of a modal
/// are exactly the part a headless test can check.
/// </summary>
public class ScreenEditReviewTests
{
    private static ConsoleKeyInfo Letter(char c, ConsoleKey key) => new(c, key, false, false, false);

    private static ConsoleKeyInfo Bare(ConsoleKey key) => new('\0', key, false, false, false);

    private static readonly string[] Two =
    {
        "world Aetherfall and its 2 characters",
        "trigger set Comms and its 15 rules, 2 characters using it",
    };

    /// <summary>
    /// The question names what went, in the words the user asked for — not a count. Same discipline as the
    /// quit prompt naming the worlds it would disconnect: a question that says what it is about is
    /// answerable, and "are you sure?" is a ritual.
    /// </summary>
    [Test]
    public async Task ItNamesEachDeletionRatherThanCountingThem()
    {
        var rendered = string.Join("\n", ScreenEditReview.Render(Two, ReviewChoice.Keep));

        await Assert.That(rendered).Contains(ScreenEditReview.Title);
        await Assert.That(rendered).Contains("world Aetherfall and its 2 characters");
        await Assert.That(rendered).Contains("trigger set Comms and its 15 rules, 2 characters using it");
    }

    /// <summary>
    /// Past a few, names stop identifying anything and only cost width — so the rest are counted, exactly
    /// as the quit prompt caps its own lists.
    /// </summary>
    [Test]
    public async Task ALongListIsCappedAndCounted()
    {
        var many = Enumerable.Range(1, 7).Select(i => $"world W{i}").ToArray();

        var rendered = string.Join("\n", ScreenEditReview.Render(many, ReviewChoice.Keep));

        await Assert.That(rendered).Contains("world W4");
        await Assert.That(rendered).DoesNotContain("world W5");
        await Assert.That(rendered).Contains("+ 3 more");
    }

    /// <summary>
    /// <b>The default is Keep, and it is drawn rather than implied</b> — the Keep chip carries the accent
    /// block and the footer names the key ⏎ is standing on. It is the opposite of the quit prompt's default
    /// for a reason that inverts cleanly: ⌃Q guards against a stray keystroke, so its default must be the
    /// answer a second stray key cannot do damage with. A deletion is not stray — it took a deliberate
    /// Delete on a deliberately selected row — so defaulting to Undo would let one careless ⏎ throw away
    /// work the user chose to do.
    /// </summary>
    [Test]
    public async Task TheDefaultIsVisiblyKeep()
    {
        var keep = string.Join("\n", ScreenEditReview.Render(Two, ReviewChoice.Keep));
        var undo = string.Join("\n", ScreenEditReview.Render(Two, ReviewChoice.Undo));

        await Assert.That(keep).Contains("▸ Keep  y");
        await Assert.That(keep).Contains("⏎ keep");
        await Assert.That(undo).Contains("▸ Undo  n");
        await Assert.That(undo).Contains("⏎ undo");
    }

    /// <summary>Both spellings are the same width, so the row does not shift as the pointer moves.</summary>
    [Test]
    public async Task TheButtonsDoNotMoveAsThePointerDoes()
    {
        var keep = ScreenEditReview.Render(Two, ReviewChoice.Keep);
        var undo = ScreenEditReview.Render(Two, ReviewChoice.Undo);

        await Assert.That(MarkupText.VisibleLength(keep[^2]))
            .IsEqualTo(MarkupText.VisibleLength(undo[^2]));
    }

    /// <summary>
    /// <c>y</c> keeps, <c>n</c> undoes, whichever button is pointed at — the letters answer the question
    /// rather than moving the pointer.
    /// </summary>
    [Test]
    public async Task TheLettersAnswerOutright()
    {
        foreach (var pointed in new[] { ReviewChoice.Keep, ReviewChoice.Undo })
        {
            await Assert.That(ScreenEditReview.Interpret(Letter('y', ConsoleKey.Y), pointed).Action)
                .IsEqualTo(ReviewAction.Keep);
            await Assert.That(ScreenEditReview.Interpret(Letter('n', ConsoleKey.N), pointed).Action)
                .IsEqualTo(ReviewAction.Undo);
        }
    }

    /// <summary>
    /// <b>Esc keeps.</b> That is the scope rule read once more rather than an arbitrary mapping: Esc never
    /// touches work that was confirmed, and these deletions were. It is also the answer a user who pressed
    /// Esc twice in a hurry will have wanted.
    /// </summary>
    [Test]
    public async Task EscapeKeeps()
    {
        await Assert.That(ScreenEditReview.Interpret(Bare(ConsoleKey.Escape), ReviewChoice.Undo).Action)
            .IsEqualTo(ReviewAction.Keep);
    }

    /// <summary>
    /// And so does Delete — the key that opened the situation, which auto-repeats under a held finger. A
    /// reading where it confirmed would make a stuck key destructive; this is the same argument that makes
    /// a second ⌃Q dismiss the quit prompt.
    /// </summary>
    [Test]
    public async Task DeleteDismissesItRatherThanConfirming()
    {
        await Assert.That(ScreenEditReview.Interpret(Bare(ConsoleKey.Delete), ReviewChoice.Undo).Action)
            .IsEqualTo(ReviewAction.Keep);
    }

    /// <summary>⏎ runs whatever is pointed at, which is how the arrows amount to anything.</summary>
    [Test]
    public async Task EnterRunsThePointedAtButton()
    {
        await Assert.That(ScreenEditReview.Interpret(Bare(ConsoleKey.Enter), ReviewChoice.Keep).Action)
            .IsEqualTo(ReviewAction.Keep);
        await Assert.That(ScreenEditReview.Interpret(Bare(ConsoleKey.Enter), ReviewChoice.Undo).Action)
            .IsEqualTo(ReviewAction.Undo);
    }

    /// <summary>←→ pick, ⇥ alternates, and each only redraws — the answer is still ⏎'s to give.</summary>
    [Test]
    public async Task TheArrowsMoveThePointerWithoutAnswering()
    {
        var right = ScreenEditReview.Interpret(Bare(ConsoleKey.RightArrow), ReviewChoice.Keep);
        await Assert.That(right).IsEqualTo(new ReviewDecision(ReviewAction.Redraw, ReviewChoice.Undo));

        var left = ScreenEditReview.Interpret(Bare(ConsoleKey.LeftArrow), ReviewChoice.Undo);
        await Assert.That(left).IsEqualTo(new ReviewDecision(ReviewAction.Redraw, ReviewChoice.Keep));

        var tab = ScreenEditReview.Interpret(Bare(ConsoleKey.Tab), ReviewChoice.Keep);
        await Assert.That(tab).IsEqualTo(new ReviewDecision(ReviewAction.Redraw, ReviewChoice.Undo));
    }

    /// <summary>
    /// Everything else is swallowed, modifiers included. A question that let stray keys through to the
    /// workspace beneath it would be collecting an answer to something the user has stopped reading.
    /// </summary>
    [Test]
    public async Task AnythingElseIsSwallowed()
    {
        foreach (var key in new[]
        {
            Letter('q', ConsoleKey.Q),
            Bare(ConsoleKey.UpArrow),
            Bare(ConsoleKey.F5),
            new ConsoleKeyInfo('\0', ConsoleKey.Q, false, false, true),  // ⌃Q
            new ConsoleKeyInfo('y', ConsoleKey.Y, false, true, false),   // Alt+y is not y
        })
        {
            await Assert.That(ScreenEditReview.Interpret(key, ReviewChoice.Keep))
                .IsEqualTo(new ReviewDecision(ReviewAction.None, ReviewChoice.Keep))
                .Because(key.Key.ToString());
        }
    }
}
