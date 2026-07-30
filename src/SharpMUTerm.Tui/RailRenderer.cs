using System.Globalization;
using SharpConsoleUI.Parsing;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Workspaces;
using static SharpMUTerm.Tui.MarkupText;

namespace SharpMUTerm.Tui;

/// <summary>
/// Renders <see cref="RailModel"/> rows into markup lines for the connection rail: a header, worlds
/// with an accent spine, characters with a connected dot and active marker, and windows with
/// unread/unsent/pane detail. Pure so the rail layout is unit-testable.
/// <para>
/// A row carrying a <see cref="RailRow.Target"/> is wrapped in a <c>[link=…]</c> span, which is how
/// clicking it switches. The span is invisible chrome: <c>[link=…]</c> emits no cell, so the rail
/// looks exactly as it did and — the part that matters beyond looks — its measured width is
/// unchanged, because <c>SharpMUTermApp.RailWidth</c> derives the sidebar's column count from the
/// widest row's <em>visible</em> width. A link that added a cell would resize the sidebar and, through
/// per-pane NAWS, misreport every connected session's pane size. The span covers the row's content
/// but never its leading indent or the empty tail out to the column edge, so a click aimed at the
/// splitter beside the rail lands on nothing.
/// </para>
/// </summary>
internal static class RailRenderer
{
    private const string DefaultAccent = "#00f5b7";

    /// <param name="rows">The rail's rows, as <see cref="RailModel"/> projects them.</param>
    /// <param name="maxWidth">
    /// The widest a row may be, in visible cells — the sidebar's own cap. A row longer than that is elided
    /// rather than left to wrap, because the sidebar's width is the widest row's <em>clamped</em>
    /// (<c>SharpMUTermApp.RailWidth</c>), so any name past the clamp — a web page's title is the easy one —
    /// would run onto a second line. A wrapped rail row is the thing the report was about.
    /// </param>
    public static List<string> Render(IReadOnlyList<RailRow> rows, int maxWidth = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var lines = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            lines.Add(Fit(row, maxWidth, RenderRow));
        }

        return lines;
    }

    private static string RenderRow(RailRow row) => row.Kind switch
    {
        RailRowKind.Header => $"[dim]┌ {Glyphs.Connections} CONNECTIONS[/]",
        RailRowKind.World => Link(row, $"[{Accent(row)}]▚[/] [bold]{Escape(row.Label)}[/]"),
        RailRowKind.Host => $"{Indent(row)}[dim]{Escape(row.Label)}[/]",
        RailRowKind.Empty => $"{Indent(row)}{Link(row, $"[dim]{Escape(row.Label)}[/]")}",
        RailRowKind.Character => Character(row),
        RailRowKind.Window => Window(row),
        _ => Escape(row.Label),
    };

    /// <summary>
    /// Renders a row, and if it does not fit, renders it again with its <see cref="RailRow.Label"/> shortened
    /// by however much it overran. The label is the only part that may give ground: the accent spine, the
    /// connected dot, the unread count, the ✎ pen and the pane column are all information. Measured with the
    /// app's own <see cref="SharpMUTermApp.MarkupWidth"/>, because that is the measure the sidebar's width is
    /// derived from — anything else could agree here and disagree where it matters.
    /// <para>
    /// <b>Only the label may vary in width, and only when it changes.</b> Everything else on a row is either
    /// one cell whatever it says (the spine, the ● / ○ dot, the ▸ active marker, the ▪ bullet) or occupies a
    /// reserved field that is blank when it has nothing to say (<see cref="UnsentFieldWidth"/>,
    /// <see cref="UnreadFieldWidth"/>). That is what stops a keystroke or a line of output resizing the
    /// sidebar and, through it, every connected server's terminal size. The pane column is the one
    /// remaining variable part and it is deliberately left so: it exists only in a split and appears when
    /// the layout changes, which is already a relayout that re-reports every pane.
    /// </para>
    /// </summary>
    private static string Fit(RailRow row, int maxWidth, Func<RailRow, string> render)
    {
        var line = render(row);
        var over = SharpMUTermApp.MarkupWidth(line) - maxWidth;
        if (over <= 0 || row.Label.Length == 0)
        {
            return line;
        }

        var elements = row.Label.EnumerateRunes().Select(r => r.ToString()).ToList();
        var keep = Math.Max(1, elements.Count - over - 1); // one cell goes to the ellipsis
        return render(row with { Label = string.Concat(elements.Take(keep)) + "…" });
    }

    /// <summary>
    /// Renders the collapsed rail (⌃B b): a ~6-col strip of per-world accent separators and, under
    /// each, its characters as a status dot + initial + unread count. Both stay clickable — an initial
    /// is the only handle a collapsed rail offers, so if it did not switch character the strip would be
    /// decoration. (It was decoration, and this comment said otherwise, until the rail was wired up.)
    /// </summary>
    public static List<string> RenderCollapsed(IReadOnlyList<RailRow> rows)
    {
        var lines = new List<string>();
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case RailRowKind.World:
                    lines.Add(Link(row, $"[{Accent(row)}]▚[/]"));
                    break;
                case RailRowKind.Character:
                    var initial = row.Label.Length > 0
                        ? Escape(row.Label.EnumerateRunes().First().ToString())
                        : "?";
                    var dot = row.Connected ? "●" : "○";
                    var name = row.Active ? $"[bold]{initial}[/]" : initial;

                    // Reserved here too. The collapsed strip is clamped to 4–10 cells, so it moves less —
                    // but it moves, and a strip that widens when a background world says something is the
                    // same reflow as the expanded rail's, on a rail chosen for taking no space.
                    lines.Add(Link(row, $"[{Accent(row)}]{dot}[/]{name}{UnreadField(row.Unread)}"));
                    break;
            }
        }

        return lines;
    }

    /// <summary>
    /// A character row: the active marker, the connected dot, the name, its unread total — and, in the
    /// same right-hand column the window rows use, the pane its session is in.
    /// <para>
    /// That column is how the pane numbering is legible from anywhere. Window rows are drawn for the
    /// active character only, so <c>pane 3</c> used to be visible only to whoever was already in it,
    /// while ⌥3 was reaching it from every other character. It is drawn on the active character's row
    /// too, deliberately: a column that appeared and vanished as you switched would be a third thing to
    /// learn, and repeating "where this character is" above its window rows is redundant rather than
    /// ambiguous — unlike <c>▪ main   main</c>, both columns here mean the same thing and say it in the
    /// one vocabulary (<c>pane N</c>).
    /// </para>
    /// <para>
    /// It costs the sidebar nothing at rest: a window row is indented one level deeper and carries the
    /// pen field as well, so it is the wider row wherever one exists, and the model leaves
    /// <see cref="RailRow.Pane"/> null on a single-pane workspace exactly as it does for windows.
    /// </para>
    /// </summary>
    private static string Character(RailRow row)
    {
        var marker = row.Active ? "[bold]▸[/]" : " ";
        var dot = row.Connected ? "●" : "○";
        var name = row.Active ? $"[bold]{Escape(row.Label)}[/]" : Escape(row.Label);
        var tail = row.Pane is { Length: > 0 } pane ? $"  [dim]{Escape(pane)}[/]" : string.Empty;
        return $"{Indent(row)}{Link(row, $"{marker} [{Accent(row)}]{dot}[/] {name}{UnreadField(row.Unread)}{tail}")}";
    }

    /// <summary>
    /// <b>Cells the sidebar keeps for a row's unsent-draft pen, whether or not there is one.</b> Two: the
    /// glyph and the space that separates it from the label.
    /// <para>
    /// This is the reported bug. The pen used to be emitted only when there was a draft, so the row grew by
    /// two cells on the <em>first keystroke</em> of every line — and <c>SharpMUTermApp.RailWidth</c> takes
    /// the sidebar's column count from its widest row, so the column grew, the panes shrank, and per-pane
    /// NAWS re-announced a new terminal size to every connected server, which reflowed the game's own
    /// output. Starting to type made the screen jump. The same reasoning is why focus is indicated by
    /// recolouring and never by spending a cell; here the cell has to be spent, so it is spent
    /// unconditionally.
    /// </para>
    /// </summary>
    private const int UnsentFieldWidth = 2;

    /// <summary>Cells kept for an unread count, blank when there is none. See <see cref="UnreadField"/>.</summary>
    private const int UnreadFieldWidth = 3;

    /// <summary>The largest count drawn in full; above it the badge reads <c>99+</c> and stops growing.</summary>
    private const int MaxUnread = 99;

    /// <summary>The pen, or the same width in blanks. See <see cref="UnsentFieldWidth"/>.</summary>
    private static string Unsent(bool unsent) =>
        unsent ? $" [#ffd700]{Glyphs.Draft}[/]" : new string(' ', UnsentFieldWidth);

    /// <summary>
    /// An unread count in a fixed-width field, right-aligned, blank at zero. Reserved for the same reason
    /// the pen is (<see cref="UnsentFieldWidth"/>) and with more urgency: unread arrives <em>unbidden from
    /// the wire</em>, so an unreserved badge resizes the sidebar — and every connected server's idea of its
    /// terminal — on a line of output the reader did not ask for, and again at 9 → 10 when it takes a
    /// second digit. The cap is what makes the field finite: a count past <see cref="MaxUnread"/> reads
    /// <c>99+</c>, which is the same three cells and the same information at a glance.
    /// </summary>
    private static string UnreadField(int unread) =>
        unread <= 0
            ? new string(' ', UnreadFieldWidth)
            : $"[#00f5b7]{Badge(unread).PadLeft(UnreadFieldWidth)}[/]";

    private static string Badge(int unread) =>
        unread > MaxUnread ? $"{MaxUnread}+" : unread.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A window row: what the window is, then — when there is anything to say — where it is.
    /// <para>
    /// The second column is the hosting pane, and it earns its place only in a split: with one pane there
    /// is one place a window can be, so the model leaves <see cref="RailRow.Pane"/> null and nothing is
    /// drawn. That is not only tidiness. The three spaces of the gap were emitted unconditionally, so a
    /// single-pane rail measured three cells wider than its content — and the rail's width is taken out of
    /// the pane area, which is what every connected session is told over NAWS.
    /// </para>
    /// <para><c>closed</c> is a state rather than a place, so it always shows.</para>
    /// </summary>
    private static string Window(RailRow row)
    {
        var name = Escape(row.Label);
        var where = row.Closed ? "closed" : row.Pane is { Length: > 0 } pane ? pane : null;

        // Two spaces, not three. The reserved badge fields sit between the label and this column and are
        // blank far more often than not, so they already hold the gap open; a third on top of them would
        // be paid for in sidebar columns, which come out of the panes. Not one, though: a populated
        // unread badge ends right here, and `2 pane 2` reads as one thing rather than two.
        var tail = where is null ? string.Empty : $"  [dim]{Escape(where)}[/]";
        return $"{Indent(row)}{Link(row, $"[dim]▪[/] {name}{Unsent(row.Unsent)}{UnreadField(row.Unread)}{tail}")}";
    }

    /// <summary>
    /// Wraps already-styled markup in the row's click target, or returns it untouched when the row has
    /// none. The target is percent-escaped by <see cref="LinkUrl"/>, which is not cosmetic: both the
    /// framework's parser and our own <c>MarkupWidth</c> read a tag by scanning to the next <c>]</c>,
    /// so a world or window name containing a bracket would otherwise end the tag early — breaking the
    /// link and, worse, leaking the rest of the target into the row as visible text that changes the
    /// rail's width.
    /// </summary>
    private static string Link(RailRow row, string content) =>
        row.Target is { Length: > 0 } target ? $"[link={LinkUrl.Escape(target)}]{content}[/]" : content;

    private static string Indent(RailRow row) => new(' ', row.Indent * 2);

    private static string Accent(RailRow row) =>
        row.Accent.Kind == TerminalColorKind.Rgb
            ? $"#{row.Accent.R:x2}{row.Accent.G:x2}{row.Accent.B:x2}"
            : DefaultAccent;
}
