using System.Globalization;

namespace SharpMUTerm.Tui;

/// <summary>
/// Renders the bar that closes a pane's restored content: the one row telling a reader that everything
/// above it is from a previous run of the client and everything below it is live.
/// <para>
/// <b>Why a bar and not dimmed text.</b> BeipMU prints "Content restored" and that is the right shape —
/// mark the <em>boundary</em>, not the content. Restoring is only worth doing if what comes back is the
/// game's own text with the game's own colours in it, so recolouring the restored lines to prove they
/// are restored would destroy the thing being restored. One row, drawn like the freeze bar (which
/// divides the same pane for the same kind of reason), costs a line and lies about nothing. The
/// restored lines also keep their original timestamps, so the timestamp column is a second, free answer
/// to "how old is this" for anyone who has it on.
/// </para>
/// <para>
/// It sits <em>below</em> the restored lines rather than above them, because that is where the reader's
/// eye already is: a pane bottom-anchors, so the boundary is what you land on, and "the previous
/// session ended here" is a statement about the row above it.
/// </para>
/// Pure, so the markup is unit-testable without a terminal.
/// </summary>
internal static class RestoreBarRenderer
{
    /// <summary>How long the trailing rule is. The same 48 cells the freeze bar draws, for the same reason.</summary>
    private const int RuleCells = 48;

    /// <summary>The label, kept as a constant so a test can look for the exact words a reader will see.</summary>
    internal const string Label = "RESTORED";

    /// <summary>
    /// The bar for <paramref name="lines"/> lines last written at <paramref name="when"/>, on an
    /// already-resolved <c>#rrggbb</c> accent.
    /// </summary>
    public static string Bar(int lines, DateTimeOffset when, string accentHex)
    {
        ArgumentException.ThrowIfNullOrEmpty(accentHex);

        // The count and the time are both here because they answer different questions, and a reader
        // asks both: "is this all of it?" (a pane that stops at the bound looks truncated, and it is —
        // saying how many says so) and "how stale is this?" (an hour ago is context; a fortnight ago is
        // a different game). Local time in the same 24-hour form the timestamp gutter uses.
        var count = lines == 1 ? "1 line" : $"{lines} lines";
        var stamp = when.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
        var rule = new string('─', RuleCells);
        return $"[{accentHex}]{Glyphs.Restored} {Label}[/] "
            + $"[dim]{MarkupText.Escape($"{count} from the previous session · {stamp}")} {rule}[/]";
    }
}
