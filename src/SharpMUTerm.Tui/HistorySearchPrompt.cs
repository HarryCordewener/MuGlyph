using SharpMUTerm.Core.Input;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>What one keystroke means to the open history surface.</summary>
internal enum HistoryAction
{
    /// <summary>Nothing. The key is swallowed and the surface stays exactly as it is.</summary>
    None,

    /// <summary>The query or the pointed-at row changed; refilter and redraw.</summary>
    Redraw,

    /// <summary>Put the pointed-at entry on the command line. It is <em>not</em> sent.</summary>
    Insert,

    /// <summary>Close, changing nothing.</summary>
    Cancel,
}

/// <summary>The answer to one keystroke: what to do, and the query and row it leaves behind.</summary>
internal readonly record struct HistoryDecision(HistoryAction Action, string Query, int Selected);

/// <summary>
/// The history surface (⌃R), minus the window: what a keystroke means to it, and what it says. Pure, for
/// the same reason <see cref="QuitPrompt"/> is — the rules and the wording are exactly the part a
/// headless test can pin, and <see cref="HistorySurface"/> is left with nothing but framework calls.
/// <para>
/// <b>One surface, both halves of the request.</b> It opens showing the whole history newest-first — the
/// plain chronological list — and typing filters it. That is the interaction ⌃P already teaches, and a
/// second, differently-shaped surface for "the list" and "the search" would be two things to learn where
/// the user asked for one thing to look at.
/// </para>
/// <para>
/// <b>⏎ inserts; it does not send.</b> The same thing <c>↑</c> does, and the reason is the live world on
/// the other end: with a filter in play the highlighted row is not always the one you had in mind, and a
/// surface that fired commands at a MU* on one keystroke would be a footgun with search attached. The
/// insert goes through <see cref="InputHistory.RecallAt"/>, so the draft it displaces is parked exactly
/// as <c>↑</c> parks it and <c>↓</c> still walks back to it.
/// </para>
/// <para>
/// <b>⌃R, not ⌃H.</b> ⌃H cannot be bound in this stack at all: <c>AnsiInputParser</c> turns byte 0x08
/// into <see cref="ConsoleKey.Backspace"/> with no Control modifier, so Ctrl+H <em>is</em> Backspace and
/// taking it would break the command line's own erase key. ⌃R is free (the app claims Q, W, N, O, P, B, F;
/// the command line reads L, A, E, K, U) and is the reverse-history-search chord in bash, zsh, fish and
/// readline generally, so it costs nothing to learn.
/// </para>
/// <para>
/// <b>The hints name only keys that work.</b> ↑↓ move (wrapping, as ⌃P's do), ⏎ inserts, Esc cancels, ⌃R
/// closes, printable characters filter and Backspace un-filters. Nothing else is bound, so nothing else
/// is advertised — see <c>HistorySearchPromptTests</c>, which presses every key the footer names.
/// </para>
/// </summary>
internal static class HistorySearchPrompt
{
    /// <summary>
    /// The keys, and the whole set of them. Every one is honoured by <see cref="Interpret"/>. ⌫ is
    /// deliberately not here: it is named on the <c>no matches</c> line instead, which is the only moment a
    /// user needs telling that the filter can be widened — and the footer has to fit
    /// <see cref="HistorySurface"/>'s minimum width, which this string is what sets.
    /// </summary>
    internal const string Hints = "type to filter · ↑↓ pick · ⏎ insert · Esc cancel · ⌃R closes";

    /// <summary>The longest an entry is drawn before it is elided, when no width is supplied.</summary>
    private const int DefaultEntryWidth = 72;

    /// <summary>
    /// What one keystroke does, and the query and row it leaves behind. <paramref name="count"/> is how
    /// many rows are currently listed, so the pointer wraps within what is actually on screen.
    /// <para>
    /// Anything unrecognised is swallowed rather than passed down: a modal surface that let stray keys
    /// through would be typing into the command line the user cannot currently see.
    /// </para>
    /// </summary>
    internal static HistoryDecision Interpret(ConsoleKeyInfo key, string query, int selected, int count)
    {
        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);

        // ⌃R is spelled out here as well as in the surface's toggle, for QuitPrompt's reason: this is
        // where "what does this keystroke mean" is answered, and a rule living only in the app's shortcut
        // table could not be read back by a test. A global shortcut runs before any window, so in the
        // running client the chord never reaches this handler — the toggle answers it, the same way.
        if (ctrl)
        {
            return key.Key == ConsoleKey.R
                ? new HistoryDecision(HistoryAction.Cancel, query, selected)
                : new HistoryDecision(HistoryAction.None, query, selected);
        }

        if (key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            return new HistoryDecision(HistoryAction.None, query, selected);
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                return new HistoryDecision(HistoryAction.Cancel, query, selected);

            case ConsoleKey.Enter:
                // With nothing listed there is nothing to insert, so ⏎ does nothing and leaves the
                // surface up: the query is what needs fixing, and Esc is the advertised way out.
                return count == 0
                    ? new HistoryDecision(HistoryAction.None, query, selected)
                    : new HistoryDecision(HistoryAction.Insert, query, selected);

            case ConsoleKey.UpArrow:
                return new HistoryDecision(HistoryAction.Redraw, query, Step(selected, -1, count));

            case ConsoleKey.DownArrow:
                return new HistoryDecision(HistoryAction.Redraw, query, Step(selected, 1, count));

            case ConsoleKey.Backspace:
                // The pointer goes back to the newest match: widening the filter changes what row 0 is.
                return query.Length == 0
                    ? new HistoryDecision(HistoryAction.None, query, selected)
                    : new HistoryDecision(HistoryAction.Redraw, query[..^1], 0);
        }

        // Typing filters. Control characters are excluded so an undecoded sequence cannot end up in the
        // query, and Tab (which arrives as one) is swallowed rather than becoming whitespace.
        if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0')
        {
            return new HistoryDecision(HistoryAction.Redraw, query + key.KeyChar, 0);
        }

        return new HistoryDecision(HistoryAction.None, query, selected);
    }

    /// <summary>Moves the pointer, wrapping — the same behaviour ⌃P's list has.</summary>
    private static int Step(int selected, int delta, int count) =>
        count == 0 ? -1 : ((selected + delta) % count + count) % count;

    /// <summary>
    /// The whole surface as markup lines: the query line, the rows newest-first with the pointed-at one
    /// filled, and the keys. <paramref name="width"/> is the surface's content width when known, which
    /// both elides over-long entries and pads the selected row's bar to the full width.
    /// <para>
    /// <paramref name="listRows"/> is how many rows the list area holds and <paramref name="first"/> which
    /// match is at the top of it, which is what makes this a viewport rather than a dump: a history of five
    /// hundred entries does not fit any terminal, and a list that simply ran off the bottom would walk the
    /// pointer somewhere the user cannot see and push the footer off the frame with it. The area is always
    /// drawn at full height — padded with blank rows when the filter narrows — so the footer does not walk
    /// up and down the screen on every keystroke. Zero means "as many rows as there are matches", which is
    /// the unit-test path.
    /// </para>
    /// </summary>
    internal static List<string> Render(
        IReadOnlyList<HistoryMatch> matches,
        string query,
        int selected,
        int total,
        string barLabel,
        int width = 0,
        int listRows = 0,
        int first = 0)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(query);

        var entryWidth = (width > 0 ? width : DefaultEntryWidth) - 4; // " ▸ " and a trailing cell
        var lines = new List<string>
        {
            SpreadLR(
                $"[{Label}]search[/] {QueryMarkup(query)}",
                $"[{Label}]{Escape(Counted(matches.Count, total))} · {Escape(barLabel)}[/]",
                width),
            string.Empty,
        };

        var body = new List<string>();
        if (total == 0)
        {
            body.Add($"[{Muted}]  nothing has been entered on this command line yet[/]");
        }
        else if (matches.Count == 0)
        {
            body.Add($"[{Muted}]  no matches — ⌫ widens the filter[/]");
        }

        var top = Math.Clamp(first, 0, Math.Max(0, matches.Count - 1));
        var last = listRows > 0 ? Math.Min(matches.Count, top + listRows - body.Count) : matches.Count;
        for (var i = top; i < last; i++)
        {
            body.Add(Row(matches[i], i == selected, entryWidth, width));
        }

        while (body.Count < listRows)
        {
            body.Add(string.Empty);
        }

        lines.AddRange(body);
        lines.Add(string.Empty);
        lines.Add($"[{Label}]{Hints}[/]");
        return lines;
    }

    /// <summary>
    /// Where the list area's top must sit for <paramref name="selected"/> to be inside it, moving as little
    /// as possible from <paramref name="first"/>. The surface owns the state; the arithmetic lives here
    /// with the renderer that consumes it, so "the pointed-at row is always drawn" is one testable claim.
    /// </summary>
    internal static int Scroll(int first, int selected, int count, int listRows)
    {
        if (listRows <= 0 || count <= listRows || selected < 0)
        {
            return 0;
        }

        var top = Math.Clamp(first, selected - listRows + 1, selected);
        return Math.Clamp(top, 0, count - listRows);
    }

    /// <summary>The visible width of the widest rendered line — used to size the surface to its content.</summary>
    internal static int MaxWidth(IReadOnlyList<string> lines)
    {
        var max = 0;
        foreach (var line in lines)
        {
            max = Math.Max(max, VisibleLength(line));
        }

        return max;
    }

    /// <summary>
    /// The query, with the accent block caret after it — the same caret the settings screens draw on an
    /// open field, so this reads as something being typed into. An empty query says what the surface is
    /// showing instead of showing nothing: the unfiltered list is a state, not an absence.
    /// </summary>
    private static string QueryMarkup(string query) =>
        query.Length == 0
            ? $"[{Ink} on {Accent}] [/] [{Label}]everything, newest first[/]"
            : $"[{Value}]{Escape(query)}[/][{Ink} on {Accent}] [/]";

    /// <summary>How many rows are listed out of how many the command line has ever held.</summary>
    private static string Counted(int shown, int total) =>
        shown == total ? $"{total} entr{(total == 1 ? "y" : "ies")}" : $"{shown} of {total}";

    private static string Row(HistoryMatch match, bool selected, int entryWidth, int width)
    {
        var (text, matchStart, matchLength) = Elide(match, entryWidth);

        if (selected)
        {
            // One continuous accent bar across the row, padded to the surface width. The matched run is
            // not separately marked here: it would be a highlight inside a highlight, and the pointer is
            // the fact this row is drawing.
            var body = $" ▸ {Escape(text)} ";
            var pad = width > VisibleLength(body) ? new string(' ', width - VisibleLength(body)) : string.Empty;
            return $"[{Ink} on {Accent}]{body}{pad}[/]";
        }

        if (matchStart < 0 || matchLength == 0)
        {
            return $"[{Value}]   {Escape(text)}[/]";
        }

        // Mark where the query matched, so a filtered list shows *why* each row is in it.
        var before = Escape(text[..matchStart]);
        var hit = Escape(text.Substring(matchStart, matchLength));
        var after = Escape(text[(matchStart + matchLength)..]);
        return $"[{Value}]   {before}[/][bold {Accent}]{hit}[/][{Value}]{after}[/]";
    }

    /// <summary>
    /// Shortens an over-long entry to the surface's width, keeping the matched run visible: a pose is
    /// hundreds of cells long, and a row clipped at the left edge would hide the very text the query
    /// found. Returns the text to draw and where the match sits inside it.
    /// </summary>
    private static (string Text, int MatchStart, int MatchLength) Elide(HistoryMatch match, int entryWidth)
    {
        const string Ellipsis = "…";
        var text = match.Text;
        if (entryWidth <= Ellipsis.Length || text.Length <= entryWidth)
        {
            return (text, match.MatchStart, match.MatchLength);
        }

        // Window the text so the match is inside it; without a match, keep the head.
        var matchEnd = match.MatchStart < 0 ? 0 : match.MatchStart + match.MatchLength;
        var start = Math.Max(0, Math.Min(matchEnd - entryWidth + Ellipsis.Length, text.Length - entryWidth));
        var length = Math.Min(entryWidth - Ellipsis.Length, text.Length - start);

        var head = start > 0 ? Ellipsis : string.Empty;
        var tail = start + length < text.Length ? Ellipsis : string.Empty;
        var shown = head + text.Substring(start, length) + tail;

        if (match.MatchStart < 0)
        {
            return (shown, -1, 0);
        }

        // Re-base the match onto the windowed text, clipping it to what is actually drawn.
        var shifted = match.MatchStart - start + head.Length;
        var visible = Math.Max(0, Math.Min(match.MatchLength, shown.Length - tail.Length - shifted));
        return shifted < 0 ? (shown, -1, 0) : (shown, shifted, visible);
    }
}
