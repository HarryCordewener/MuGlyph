using SharpConsoleUI.Parsing;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Workspaces;
using static SharpMUTerm.Tui.MarkupText;

namespace SharpMUTerm.Tui;

/// <summary>
/// Renders <see cref="RailModel"/> rows into markup lines for the connection rail: a header, worlds
/// with an accent spine, characters with a connected dot and active marker, and windows with
/// unread/unsent detail and the chord that goes to them. Pure so the rail layout is unit-testable.
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
    /// connected dot, the unread count, the ✎ pen and the chord column are all information. Measured with the
    /// app's own <see cref="SharpMUTermApp.MarkupWidth"/>, because that is the measure the sidebar's width is
    /// derived from — anything else could agree here and disagree where it matters.
    /// <para>
    /// <b>Only the label may vary in width, and only when it changes.</b> Everything else on a row is either
    /// one cell whatever it says (the spine, the ● / ○ dot, the ▸ active marker, the ▪ bullet) or occupies a
    /// reserved field that is blank when it has nothing to say (<see cref="UnsentFieldWidth"/>,
    /// <see cref="UnreadFieldWidth"/>). That is what stops a keystroke or a line of output resizing the
    /// sidebar and, through it, every connected server's terminal size. The chord column is the one
    /// remaining variable part and it is deliberately left so: it is absent only while the workspace holds
    /// a single window, and it appears when a second one opens — which is a structural change that
    /// rebuilds the pane area and re-reports every pane anyway.
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
    /// same right-hand column the window rows use, the chord that goes to this character.
    /// <para>
    /// That column is how the window numbering is legible from anywhere. Window rows are drawn for the
    /// active character only, so a background character's windows were numbered, one keystroke away, and
    /// unnamed by anything on the screen. It is drawn on the active character's row too, deliberately: a
    /// column that appeared and vanished as you switched would be a third thing to learn, and repeating
    /// the digit above that character's own window row is redundant rather than ambiguous — unlike
    /// <c>▪ main   main</c>, both cells here are the same chord to the same window, spelt the one way.
    /// </para>
    /// <para>
    /// It costs the sidebar nothing at rest: a window row is indented one level deeper and carries the
    /// pen field as well, so it is the wider row wherever one exists, and the model leaves
    /// <see cref="RailRow.Chord"/> null on a single-window workspace exactly as it does for windows.
    /// </para>
    /// </summary>
    private static string Character(RailRow row)
    {
        var marker = row.Active ? "[bold]▸[/]" : " ";
        var dot = row.Connected ? "●" : "○";
        var name = row.Active ? $"[bold]{Escape(row.Label)}[/]" : Escape(row.Label);
        var tail = row.Chord is { Length: > 0 } chord ? $" [dim]{Escape(chord)}[/]" : string.Empty;
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

    /// <summary>
    /// Cells kept for an unread count, blank when there is none. See <see cref="UnreadField"/>. It is
    /// <see cref="UnreadBadge.FieldWidth"/> because the badge's own cap is what makes the field finite —
    /// the two numbers are one fact and may not be written down twice.
    /// </summary>
    private const int UnreadFieldWidth = UnreadBadge.FieldWidth;

    /// <summary>The pen, or the same width in blanks. See <see cref="UnsentFieldWidth"/>.</summary>
    private static string Unsent(bool unsent) =>
        unsent ? $" [#ffd700]{Glyphs.Draft}[/]" : new string(' ', UnsentFieldWidth);

    /// <summary>
    /// An unread count in a fixed-width field, right-aligned, blank at zero. Reserved for the same reason
    /// the pen is (<see cref="UnsentFieldWidth"/>) and with more urgency: unread arrives <em>unbidden from
    /// the wire</em>, so an unreserved badge resizes the sidebar — and every connected server's idea of its
    /// terminal — on a line of output the reader did not ask for, and again at 9 → 10 when it takes a
    /// second digit. The cap is what makes the field finite: a count past <see cref="UnreadBadge.Max"/>
    /// reads <c>99+</c>, which is the same three cells and the same information at a glance.
    /// <para>
    /// Both the wording and the colour come from <see cref="UnreadBadge"/>, which the pane tab labels draw
    /// from as well, so the sidebar and the strip cannot come to say different things about one count.
    /// </para>
    /// </summary>
    private static string UnreadField(int unread) =>
        unread <= 0
            ? new string(' ', UnreadFieldWidth)
            : $"[{UnreadBadge.Tint}]{UnreadBadge.Format(unread).PadLeft(UnreadFieldWidth)}[/]";

    /// <summary>
    /// A window row: what the window is, then — when there is anything to say — how to get to it.
    /// <para>
    /// The second column is the <c>⌥N</c> that goes to this window, and it earns its place only once the
    /// workspace holds a second window: with one, there is one place to be, so the model leaves
    /// <see cref="RailRow.Chord"/> null and nothing is drawn. That is not only tidiness. The gap was once
    /// emitted unconditionally, so a rail with nothing to say in this column measured three cells wider
    /// than its content — and the rail's width is taken out of the pane area, which is what every
    /// connected session is told over NAWS.
    /// </para>
    /// <para>
    /// It used to be the hosting <em>pane</em>, from when ⌥N named panes. A window past the ninth has no
    /// chord and so shows nothing here, which is the honest reading: the row is still clickable and still
    /// reachable by ⌃N and the tab strip, and a column claiming a key that would go somewhere else is the
    /// one thing this numbering exists to prevent.
    /// </para>
    /// <para><c>closed</c> is a state rather than a destination, so it always shows.</para>
    /// </summary>
    private static string Window(RailRow row)
    {
        var name = Escape(row.Label);
        var where = row.Closed ? "closed" : row.Chord is { Length: > 0 } chord ? chord : null;

        // One space. The reserved badge fields sit between the label and this column and are blank far more
        // often than not, so they already hold the gap open; anything on top of them is paid for in sidebar
        // columns, which come out of the panes. Two used to be needed because the column said `pane 2` and a
        // populated unread badge ending right here made `2 pane 2` read as one thing; the sigil in `⌥2`
        // now does that work in a cell that carries meaning of its own.
        var tail = where is null ? string.Empty : $" [dim]{Escape(where)}[/]";
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
