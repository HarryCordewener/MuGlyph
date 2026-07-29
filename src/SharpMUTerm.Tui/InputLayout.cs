using SharpMUTerm.Core.Configuration;

namespace SharpMUTerm.Tui;

/// <summary>
/// One visual row of a wrapped command line: where it starts in the buffer text and how many
/// characters of it the row covers. The rows partition the text exactly — no character is dropped at a
/// break and none is duplicated — which is what lets the caret be mapped to a row and a column, and
/// back, without a second model of where the breaks fell.
/// </summary>
/// <param name="Start">The row's first character, as an index into the whole buffer.</param>
/// <param name="Length">How many characters the row covers, excluding the newline that ended it.</param>
internal readonly record struct InputRow(int Start, int Length)
{
    /// <summary>One past the row's last character.</summary>
    internal int End => Start + Length;
}

/// <summary>
/// The command line's geometry: how its text breaks into rows at a given width, how tall the control
/// is for that many rows, and how far it has scrolled when there are more rows than height.
/// <para>
/// It is a static class of its own, taking ints and returning ints, because every question the input
/// area has to answer about its own size is answerable without a terminal — and the answers are the
/// ones a maintainer reports as wrong ("it doesn't grow", "it only goes sideways"). Keeping them here
/// means the grow-and-cap behaviour is asserted directly rather than inferred from a rendered frame.
/// </para>
/// </summary>
internal static class InputLayout
{
    /// <summary>
    /// Breaks <paramref name="text"/> into the rows it occupies at <paramref name="width"/> columns.
    /// Newlines break hard; everything else wraps at the last space that fits, or mid-word when a word
    /// is longer than the line. The space a wrap breaks on stays at the end of the row it ended, so the
    /// rows still add up to the text.
    /// <para>
    /// A final row that fills the width exactly is followed by an empty one. That row is where the next
    /// character goes and where the caret sits until it is typed, and without it a caret at the end of
    /// a full row would have nowhere on screen to be.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<InputRow> Wrap(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);

        var rows = new List<InputRow>();
        if (width <= 0)
        {
            rows.Add(new InputRow(0, text.Length));
            return rows;
        }

        var lineStart = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? text.Length : newline;
            WrapLine(text, lineStart, lineEnd, width, rows);

            if (newline < 0)
            {
                break;
            }

            lineStart = newline + 1;
        }

        if (rows[^1].Length == width)
        {
            rows.Add(new InputRow(text.Length, 0));
        }

        return rows;
    }

    /// <summary>Breaks one newline-free span into rows, appending them to <paramref name="rows"/>.</summary>
    private static void WrapLine(string text, int start, int end, int width, List<InputRow> rows)
    {
        var pos = start;
        while (true)
        {
            var remaining = end - pos;
            if (remaining <= width)
            {
                rows.Add(new InputRow(pos, remaining));
                return;
            }

            // The last space inside the row, kept on it: breaking after the space means the next row
            // starts on a word rather than on the space that separated it.
            var space = text.LastIndexOf(' ', pos + width - 1, width);
            var length = space >= pos ? space - pos + 1 : width;
            rows.Add(new InputRow(pos, length));
            pos += length;
        }
    }

    /// <summary>
    /// The row and column a caret at <paramref name="caret"/> occupies. A caret exactly on a break
    /// belongs to the row that starts there — where the next character will appear — rather than to the
    /// end of the row before it.
    /// </summary>
    internal static (int Row, int Column) Caret(IReadOnlyList<InputRow> rows, int caret)
    {
        ArgumentNullException.ThrowIfNull(rows);

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (caret >= rows[i].Start)
            {
                return (i, Math.Min(caret - rows[i].Start, rows[i].Length));
            }
        }

        return (0, 0);
    }

    /// <summary>
    /// The buffer offset a row and column name — the inverse of <see cref="Caret"/>, and what ↑/↓ move
    /// the caret to. The column is clamped to the row, so moving down onto a shorter row lands at its
    /// end rather than past it.
    /// </summary>
    internal static int Offset(IReadOnlyList<InputRow> rows, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var index = Math.Clamp(row, 0, rows.Count - 1);
        return rows[index].Start + Math.Clamp(column, 0, rows[index].Length);
    }

    /// <summary>
    /// How many lines tall the control is for <paramref name="rows"/> rows of text: at least
    /// <paramref name="min"/> so an empty command line still reads as one, at most
    /// <paramref name="max"/> so a long paste cannot swallow the output window. Both bounds are
    /// themselves clamped to <see cref="InputSettings.MinRows"/>..<see cref="InputSettings.MaxRowCeiling"/>,
    /// and a maximum below the minimum is raised to it rather than inverting the range — a hand-edited
    /// configuration should be surprising, not broken.
    /// </summary>
    internal static int Height(int rows, int min, int max)
    {
        var floor = Math.Clamp(min, InputSettings.MinRows, InputSettings.MaxRowCeiling);
        var ceiling = Math.Clamp(max, floor, InputSettings.MaxRowCeiling);
        return Math.Clamp(rows, floor, ceiling);
    }

    /// <summary>
    /// The first row to draw so the caret is on screen: unchanged while the caret is already visible,
    /// otherwise the least scroll that brings it back. Zero whenever the text fits, so a command line
    /// that shrinks back under its cap never keeps a stale offset.
    /// </summary>
    internal static int Scroll(int caretRow, int rows, int height, int scroll)
    {
        if (rows <= height || height <= 0)
        {
            return 0;
        }

        var clamped = Math.Clamp(scroll, 0, rows - height);
        if (caretRow < clamped)
        {
            return caretRow;
        }

        return caretRow >= clamped + height ? caretRow - height + 1 : clamped;
    }
}
