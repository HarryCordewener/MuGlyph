using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>The button the confirmation is pointing at — the one ⏎ runs.</summary>
internal enum QuitChoice
{
    /// <summary>Close the prompt and carry on. The default; see <see cref="QuitPrompt"/> for why.</summary>
    Stay,

    /// <summary>End the client.</summary>
    Quit,
}

/// <summary>What one keystroke means to the open confirmation.</summary>
internal enum QuitAction
{
    /// <summary>Nothing. The key is swallowed and the prompt stays exactly as it is.</summary>
    None,

    /// <summary>The pointed-at button moved; redraw.</summary>
    Redraw,

    /// <summary>Dismiss the prompt and leave the client running.</summary>
    Stay,

    /// <summary>Confirmed — end the client.</summary>
    Quit,
}

/// <summary>The answer to one keystroke: what to do, and which button is pointed at afterwards.</summary>
internal readonly record struct QuitDecision(QuitAction Action, QuitChoice Selection);

/// <summary>
/// What a quit would end, as of the keystroke that asked. The app gathers it (only the app can see
/// sessions, windows and the open settings screen) and the prompt only renders it, so "what the
/// question says, given N connections and M drafts" is answerable without a terminal.
/// </summary>
/// <param name="ConnectedWorlds">The worlds whose connections a quit drops, by name.</param>
/// <param name="Drafts">
/// How many unsent lines there are, counted per command line rather than per window: the second bar
/// holds a line of its own, and a window counted once would hide it.
/// </param>
/// <param name="DraftWindows">The windows those drafts are sitting in, by title.</param>
/// <param name="OpenScreen">The settings screen open over the workspace, or null when none is.</param>
/// <param name="UnsavedEdits">How many edits that screen has made that were never saved.</param>
internal readonly record struct QuitFacts(
    IReadOnlyList<string> ConnectedWorlds,
    int Drafts,
    IReadOnlyList<string> DraftWindows,
    string? OpenScreen,
    int UnsavedEdits)
{
    /// <summary>A client holding nothing: nothing connected, nothing typed, no screen open.</summary>
    internal static QuitFacts Nothing { get; } =
        new(Array.Empty<string>(), 0, Array.Empty<string>(), null, 0);
}

/// <summary>
/// The quit confirmation, minus the window: what a keystroke means to it, and what it says. Pure, for
/// the same reason <see cref="SettingsSession"/> is — the rules of a modal are exactly the part that a
/// headless test can check, and <see cref="QuitOverlay"/> is left with nothing but the framework calls.
/// <para>
/// <b>The default is Stay.</b> ⌃Q sits one key from ⌃W (close window) and is the terminal's own XON, so
/// the accident this prompt exists to catch is a chord arriving that nobody meant — and a prompt
/// defaulting to Quit is defeated by any second stray keystroke, which is the same accident again. The
/// deliberate quit pays one key for it (<c>y</c>, never more than ⏎ would have cost), and the default is
/// drawn rather than implied: the Stay chip carries the accent block, and the footer names the key ⏎ is
/// currently standing on.
/// </para>
/// <para>
/// <b>⌃Q while the prompt is up dismisses it</b> — the same answer as <c>n</c>. Every other surface in
/// this client closes on the chord that opened it (⌃P, and each screen's own F-key), and the alternative
/// reading — a second ⌃Q confirms — would make <c>⌃Q ⌃Q</c> an unguarded quit, which a held key repeats
/// on its own. The impatient user is served by <c>y</c>, which is one keystroke and no further away.
/// </para>
/// </summary>
internal static class QuitPrompt
{
    /// <summary>How many names a consequence line spells out before it starts counting instead.</summary>
    private const int MaxNames = 3;

    /// <summary>
    /// What one keystroke does, and where it leaves the pointer. <c>y</c>/<c>n</c> answer outright
    /// whatever is pointed at, Esc is the way out of every modal state in this client and stays here
    /// too, ⏎ runs the pointed-at button, and ←/→/⇥ move between them — bindings that are only live
    /// inside this modal, where nothing else is listening for an arrow (unprefixed, ←→ belong to the
    /// command line's caret and ↑↓ to its history).
    /// <para>
    /// Anything else is swallowed. A confirmation that let stray keys through to the workspace beneath
    /// it would be answering a question the user has stopped looking at.
    /// </para>
    /// </summary>
    internal static QuitDecision Interpret(ConsoleKeyInfo key, QuitChoice selected)
    {
        // Ctrl+Q is spelled out here as well as in the overlay's toggle, because this type is where the
        // question "what does this keystroke mean" is answered and a rule that only existed in the app's
        // shortcut table could not be read back by a test.
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            return key.Key == ConsoleKey.Q
                ? new QuitDecision(QuitAction.Stay, selected)
                : new QuitDecision(QuitAction.None, selected);
        }

        if (key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            return new QuitDecision(QuitAction.None, selected);
        }

        switch (key.Key)
        {
            case ConsoleKey.Y:
                return new QuitDecision(QuitAction.Quit, QuitChoice.Quit);

            case ConsoleKey.N:
            case ConsoleKey.Escape:
                return new QuitDecision(QuitAction.Stay, QuitChoice.Stay);

            case ConsoleKey.Enter:
                return new QuitDecision(
                    selected == QuitChoice.Quit ? QuitAction.Quit : QuitAction.Stay, selected);

            case ConsoleKey.LeftArrow:
                return new QuitDecision(QuitAction.Redraw, QuitChoice.Quit);

            case ConsoleKey.RightArrow:
                return new QuitDecision(QuitAction.Redraw, QuitChoice.Stay);

            case ConsoleKey.Tab:
                return new QuitDecision(
                    QuitAction.Redraw, selected == QuitChoice.Quit ? QuitChoice.Stay : QuitChoice.Quit);
        }

        // A terminal that reports no ConsoleKey for a letter still reports the character.
        return char.ToLowerInvariant(key.KeyChar) switch
        {
            'y' => new QuitDecision(QuitAction.Quit, QuitChoice.Quit),
            'n' => new QuitDecision(QuitAction.Stay, QuitChoice.Stay),
            _ => new QuitDecision(QuitAction.None, selected),
        };
    }

    /// <summary>
    /// What quitting now would cost, one plain sentence per fact, and empty when it would cost nothing.
    /// A bare "are you sure?" is a ritual; this client knows what it is holding, so the question names it
    /// and becomes answerable.
    /// </summary>
    internal static IReadOnlyList<string> Consequences(QuitFacts facts)
    {
        var lines = new List<string>();

        if (facts.ConnectedWorlds.Count > 0)
        {
            var n = facts.ConnectedWorlds.Count;
            lines.Add($"{n} world{Plural(n)} connected — {Names(facts.ConnectedWorlds)}");
        }

        if (facts.Drafts > 0)
        {
            var where = facts.DraftWindows.Count > 0 ? $" — {Names(facts.DraftWindows)}" : string.Empty;
            lines.Add($"{facts.Drafts} unsent draft{Plural(facts.Drafts)}{where}");
        }

        // An open screen with nothing typed into it loses nothing, so it is not mentioned: a line that
        // appeared whether or not there was anything to lose would be read past on the occasion it counts.
        if (facts.OpenScreen is { Length: > 0 } screen && facts.UnsavedEdits > 0)
        {
            lines.Add($"{screen} is open — {facts.UnsavedEdits} unsaved edit{Plural(facts.UnsavedEdits)}");
        }

        return lines;
    }

    /// <summary>
    /// The whole surface as markup lines: the question, what it would cost, the two buttons with the
    /// pointed-at one filled, and the keys that answer it.
    /// </summary>
    internal static List<string> Render(QuitFacts facts, QuitChoice selected)
    {
        var lines = new List<string> { $"[bold {Value}]Quit SharpMUTerm?[/]", string.Empty };

        var consequences = Consequences(facts);
        if (consequences.Count == 0)
        {
            // Still worth saying: "nothing is connected and nothing is unsent" is the answer to the
            // question the prompt just asked, and it is the state in which y is safe without reading on.
            lines.Add($"[{Muted}]Nothing is connected and nothing is unsent.[/]");
        }
        else
        {
            foreach (var consequence in consequences)
            {
                lines.Add($"[{Accent}]●[/] [{Value}]{Escape(consequence)}[/]");
            }
        }

        lines.Add(string.Empty);
        lines.Add($"{Chip("Quit  y", selected == QuitChoice.Quit)}   {Chip("Stay  n", selected == QuitChoice.Stay)}");
        lines.Add($"[{Label}]{Hints(selected)}[/]");
        return lines;
    }

    /// <summary>The keys, including what ⏎ means <em>right now</em> — the second drawing of the default.</summary>
    private static string Hints(QuitChoice selected) =>
        $"←→ ⇥ pick · ⏎ {(selected == QuitChoice.Quit ? "quit" : "stay")} · Esc stays · ⌃Q again stays";

    /// <summary>
    /// One button. Both spellings are the same width, so the row does not shift as the pointer moves —
    /// the filled chip is the affordance, not the geometry.
    /// </summary>
    private static string Chip(string label, bool chosen) =>
        chosen
            ? $"[{Ink} on {Accent}] ▸ {Escape(label)} [/]"
            : $"[{Label} on {EditBg}]   {Escape(label)} [/]";

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    /// <summary>Names, capped: past a few they stop identifying anything and only cost the line's width.</summary>
    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= MaxNames
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(MaxNames))} +{names.Count - MaxNames} more";
}
