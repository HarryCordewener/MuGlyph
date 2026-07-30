using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>The button the review is pointing at — the one ⏎ runs.</summary>
internal enum ReviewChoice
{
    /// <summary>Keep the deletions. The default; see <see cref="ScreenEditReview"/> for why.</summary>
    Keep,

    /// <summary>Put them back.</summary>
    Undo,
}

/// <summary>What one keystroke means to the open review.</summary>
internal enum ReviewAction
{
    /// <summary>Nothing. The key is swallowed and the prompt stays exactly as it is.</summary>
    None,

    /// <summary>The pointed-at button moved; redraw.</summary>
    Redraw,

    /// <summary>Keep the deletions and finish closing the screen.</summary>
    Keep,

    /// <summary>Put the deletions back, then finish closing the screen.</summary>
    Undo,
}

/// <summary>The answer to one keystroke: what to do, and which button is pointed at afterwards.</summary>
internal readonly record struct ReviewDecision(ReviewAction Action, ReviewChoice Selection);

/// <summary>
/// The one question a settings screen asks on the way out: <em>you deleted these — keep them?</em> Pure,
/// exactly as <see cref="QuitPrompt"/> is and for the same reason — the rules of a modal are the part a
/// headless test can check, which leaves <see cref="EditReviewOverlay"/> with nothing but framework calls.
/// <para>
/// <b>It is only ever asked when something was deleted.</b> Committed field edits, flipped checkboxes and
/// added rows raise nothing at all, and a clean screen closes instantly on Esc. That is deliberate and it
/// is most of the point: a confirmation on every close is one people learn to dismiss without reading,
/// which is exactly how somebody eventually dismisses the one that mattered.
/// </para>
/// <para>
/// <b>The default is Keep</b>, which is the opposite of the quit prompt's default and for a reason that
/// inverts cleanly. ⌃Q guards against a <em>stray keystroke</em>, so its default has to be the answer a
/// second stray keystroke can't do damage with. A deletion is not stray: it took a deliberate Delete on a
/// deliberately selected row, possibly several times over. This prompt is a review of work the user chose
/// to do, not a second guess at whether they meant it — so the default answer is the one that respects
/// it, and undoing is a key away. Defaulting to Undo would let one careless ⏎ throw away three deliberate
/// deletions, which is the same accident this whole change is about, pointed the other way.
/// </para>
/// <para>
/// Both answers <b>close the screen</b>. Esc was a navigation gesture — the user is leaving — and
/// hijacking it into "actually, stay here" would answer a question they didn't ask. ⏎ Save is the way to
/// close while skipping this question, and it means keep, which is the same answer as the default.
/// </para>
/// </summary>
internal static class ScreenEditReview
{
    /// <summary>How many deletions are spelled out before the list starts counting instead.</summary>
    private const int MaxNames = 4;

    /// <summary>
    /// What one keystroke does, and where it leaves the pointer. <c>y</c>/<c>n</c> answer outright, ⏎ runs
    /// the pointed-at button, ←/→/⇥ move between them, and <b>Esc keeps</b> — which is not an arbitrary
    /// mapping but the scope rule read once more: Esc never touches work that was confirmed, and these
    /// deletions were. It is also the answer a user who pressed Esc twice in a hurry will have wanted.
    /// <para>
    /// <b>Delete dismisses it too</b>, with the same answer, for the reason a second ⌃Q dismisses the quit
    /// prompt: Delete is the key that opened the situation, it auto-repeats under a held finger, and a
    /// reading where it confirmed would make a stuck key destructive.
    /// </para>
    /// <para>
    /// Anything else is swallowed. A question that let stray keys through to the screen beneath it would
    /// be collecting an answer to something the user has stopped looking at.
    /// </para>
    /// </summary>
    internal static ReviewDecision Interpret(ConsoleKeyInfo key, ReviewChoice selected)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) || key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            return new ReviewDecision(ReviewAction.None, selected);
        }

        switch (key.Key)
        {
            case ConsoleKey.Y:
                return new ReviewDecision(ReviewAction.Keep, ReviewChoice.Keep);

            case ConsoleKey.N:
                return new ReviewDecision(ReviewAction.Undo, ReviewChoice.Undo);

            case ConsoleKey.Escape:
            case ConsoleKey.Delete:
                return new ReviewDecision(ReviewAction.Keep, ReviewChoice.Keep);

            case ConsoleKey.Enter:
                return new ReviewDecision(
                    selected == ReviewChoice.Undo ? ReviewAction.Undo : ReviewAction.Keep, selected);

            case ConsoleKey.LeftArrow:
                return new ReviewDecision(ReviewAction.Redraw, ReviewChoice.Keep);

            case ConsoleKey.RightArrow:
                return new ReviewDecision(ReviewAction.Redraw, ReviewChoice.Undo);

            case ConsoleKey.Tab:
                return new ReviewDecision(
                    ReviewAction.Redraw, selected == ReviewChoice.Undo ? ReviewChoice.Keep : ReviewChoice.Undo);
        }

        // A terminal that reports no ConsoleKey for a letter still reports the character.
        return char.ToLowerInvariant(key.KeyChar) switch
        {
            'y' => new ReviewDecision(ReviewAction.Keep, ReviewChoice.Keep),
            'n' => new ReviewDecision(ReviewAction.Undo, ReviewChoice.Undo),
            _ => new ReviewDecision(ReviewAction.None, selected),
        };
    }

    /// <summary>What the question calls itself.</summary>
    internal const string Title = "Keep these deletions?";

    /// <summary>
    /// The whole surface as markup lines: the question, what was deleted by name, the two buttons with
    /// the pointed-at one filled, and the keys that answer it.
    /// </summary>
    internal static List<string> Render(IReadOnlyList<string> deletions, ReviewChoice selected)
    {
        ArgumentNullException.ThrowIfNull(deletions);

        var lines = new List<string> { $"[bold {Value}]{Title}[/]", string.Empty };

        var named = deletions.Count <= MaxNames ? deletions.Count : MaxNames;
        for (var i = 0; i < named; i++)
        {
            lines.Add($"[{Warn}]✕[/] [{Value}]{Escape(deletions[i])}[/]");
        }

        if (deletions.Count > named)
        {
            lines.Add($"[{Muted}]  + {(deletions.Count - named).ToString(System.Globalization.CultureInfo.InvariantCulture)} more[/]");
        }

        lines.Add(string.Empty);
        lines.Add($"{Chip("Keep  y", selected == ReviewChoice.Keep)}   {Chip("Undo  n", selected == ReviewChoice.Undo)}");
        lines.Add($"[{Label}]{Hints(selected)}[/]");
        return lines;
    }

    /// <summary>The keys, including what ⏎ means <em>right now</em> — the second drawing of the default.</summary>
    private static string Hints(ReviewChoice selected) =>
        $"←→ ⇥ pick · ⏎ {(selected == ReviewChoice.Undo ? "undo" : "keep")} · Esc keeps";

    /// <summary>
    /// One button. Both spellings are the same width, so the row does not shift as the pointer moves —
    /// the filled chip is the affordance, not the geometry. Same shape as the quit prompt's, on purpose:
    /// this client has one look for "answer this question".
    /// </summary>
    private static string Chip(string label, bool chosen) =>
        chosen
            ? $"[{Ink} on {Accent}] ▸ {Escape(label)} [/]"
            : $"[{Label} on {EditBg}]   {Escape(label)} [/]";
}
