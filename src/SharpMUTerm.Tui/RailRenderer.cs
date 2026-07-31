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

        // Whether the chord column is drawn, decided once for the whole rail and **per row kind**.
        //
        // Reserved, because a field that costs a cell only when it has something in it resizes the
        // sidebar — and the sidebar's width comes out of the pane area, which every connected server is
        // told over NAWS (see UnsentFieldWidth for the reported instance of that bug). Within a kind the
        // width therefore does not move as unread arrives, as a draft is typed, or as the ⌥J/⌥K pair
        // travels from row to row on a character switch.
        //
        // Per kind, because the two kinds carry different mechanics and one of them is often empty: a
        // window row's chord is the ⌥N numbering, a character row's is the cycle, and with fewer than two
        // characters open no character row can have one at all. Reserving across both spent three cells
        // on every character row of a client with one character open — which is the common case, and the
        // opposite of the complaint this layout change exists to answer.
        var reserveWindow = rows.Any(r => r.Kind == RailRowKind.Window && r.Chord is { Length: > 0 });
        var reserveCharacter = rows.Any(r => r.Kind == RailRowKind.Character && r.Chord is { Length: > 0 });

        var lines = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            lines.Add(Fit(row, maxWidth, r => RenderRow(r, reserveWindow, reserveCharacter)));
        }

        return lines;
    }

    private static string RenderRow(RailRow row, bool reserveWindow, bool reserveCharacter) => row.Kind switch
    {
        RailRowKind.Header => $"[dim]┌ {Glyphs.Connections} CONNECTIONS[/]",
        RailRowKind.World => Link(row, $"[{Accent(row)}]▚[/] [bold]{Escape(row.Label)}[/]"),
        RailRowKind.Host => $"{Indent(row)}[dim]{Escape(row.Label)}[/]",
        RailRowKind.Empty => $"{Indent(row)}{Link(row, $"[dim]{Escape(row.Label)}[/]")}",
        RailRowKind.Character => Character(row, reserveCharacter),
        RailRowKind.Window => Window(row, reserveWindow),
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
    /// A character row: its chord, the active marker, the connected dot, the name and its unread total.
    /// <para>
    /// <b>The chord leads, in the same column the window rows put theirs.</b> It is <c>⌥J</c> on the
    /// character one step forward in the cycle and <c>⌥K</c> one step back — the only two that are a
    /// single keystroke away — and blank on everybody else, including the row you are standing on, whose
    /// <c>▸</c> already says so. It used to be the chord of that character's own <em>window</em>, from
    /// when window numbering ran across the whole workspace; scoped to the active character, that printed
    /// <c>⌥1</c> against every character on the screen, which is the confusion this replaced.
    /// </para>
    /// <para>
    /// <b>Reserved, and on the left.</b> The field is <see cref="ChordFieldWidth"/> cells whether or not
    /// there is a chord, so a row does not change width when the cycle moves — the same rule the pen and
    /// the unread count follow, and for the same reason: the rail's width is its widest row, the sidebar
    /// takes its columns out of the pane area, and every connected server is told its pane's size. And it
    /// is on the left because the reader's complaint was the gap: with the chord at the end of the row it
    /// sat behind two blank status fields, five cells of nothing between a window's name and the key that
    /// goes to it.
    /// </para>
    /// </summary>
    private static string Character(RailRow row, bool reserve)
    {
        var marker = row.Active ? "[bold]▸[/]" : " ";
        var dot = row.Connected ? "●" : "○";
        var name = row.Active ? $"[bold]{Escape(row.Label)}[/]" : Escape(row.Label);
        return $"{Indent(row)}{ChordField(row.Chord, reserve)}"
            + Link(row, $"{marker} [{Accent(row)}]{dot}[/] {name}{UnreadField(row.Unread)}");
    }

    /// <summary>
    /// <b>Cells the sidebar keeps for a row's chord, whether or not it has one.</b> Three: the sigil, one
    /// digit or letter, and the space that separates it from the row's own glyph.
    /// <para>
    /// Reserved for the reason <see cref="UnsentFieldWidth"/> and <see cref="UnreadFieldWidth"/> are — a
    /// cell that appears only when there is something to say resizes the sidebar, and the sidebar's width
    /// comes out of the panes, which every connected server is told over NAWS. This one moves on events a
    /// reader does not think of as structural: a capture window opening past the ninth loses its chord, and
    /// the ⌥J/⌥K pair moves from row to row on every character switch.
    /// </para>
    /// </summary>
    private const int ChordFieldWidth = 3;

    /// <summary>
    /// A chord in its fixed-width field, the same width in blanks when this row has none, or nothing at
    /// all when no row in the rail has one. Outside the row's <see cref="Link"/> span deliberately: it is
    /// chrome that names a key rather than part of the thing the row points at, so keeping it out leaves
    /// the click target on the name.
    /// </summary>
    private static string ChordField(string? chord, bool reserve)
    {
        if (!reserve)
        {
            return string.Empty;
        }

        if (chord is not { Length: > 0 } value)
        {
            return new string(' ', ChordFieldWidth);
        }

        var escaped = Escape(value);
        var pad = Math.Max(1, ChordFieldWidth - SharpMUTermApp.MarkupWidth(escaped));
        return $"[dim]{escaped}[/]{new string(' ', pad)}";
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
    /// A window row: how you get to it, then what it is, then its badges.
    /// <para>
    /// <b>The chord leads, in the same reserved column the character rows use.</b> That is the reported
    /// complaint — "there is still way too much room after a window's name before it hits 'alt-1'". The
    /// gap was the two badge fields, which are blank far more often than not and sat between the name and
    /// the key. They cannot be removed (see <see cref="UnsentFieldWidth"/>: a field that costs a cell only
    /// when it has something to say resizes the sidebar on a keystroke or a line of output, and the
    /// sidebar's width comes out of the panes, which every connected server is told over NAWS) — so the
    /// chord moved to the front instead, where nothing blank separates it from the name it belongs to,
    /// and the badges ended up at the right edge where status belongs. The row's measured width is
    /// unchanged by the move; only the order is.
    /// </para>
    /// <para>
    /// The column earns its place only once the character holds a second window: with one, there is one
    /// place to be, so the model leaves <see cref="RailRow.Chord"/> null and the field is not drawn at
    /// all. A window past the ninth has no chord and shows blanks, which is the honest reading — the row
    /// is still clickable and still reachable by ⌃N and the tab strip, and a column claiming a key that
    /// would go somewhere else is the one thing this numbering exists to prevent.
    /// </para>
    /// <para>
    /// <c>closed</c> is a state rather than a destination, so it is drawn where the badges are rather than
    /// in the chord's field: a closed window has no chord, and putting the word where a key goes would be
    /// the two-meanings-in-one-column mistake again.
    /// </para>
    /// </summary>
    private static string Window(RailRow row, bool reserve)
    {
        var name = Escape(row.Label);
        var closed = row.Closed ? " [dim]closed[/]" : string.Empty;
        return $"{Indent(row)}{ChordField(row.Closed ? null : row.Chord, reserve)}"
            + Link(row, $"[dim]▪[/] {name}{Unsent(row.Unsent)}{UnreadField(row.Unread)}{closed}");
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
