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

    public static List<string> Render(IReadOnlyList<RailRow> rows)
    {
        var lines = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            lines.Add(row.Kind switch
            {
                RailRowKind.Header => $"[dim]┌ {Glyphs.Connections} CONNECTIONS[/]",
                RailRowKind.World => Link(row, $"[{Accent(row)}]▚[/] [bold]{Escape(row.Label)}[/]"),
                RailRowKind.Host => $"{Indent(row)}[dim]{Escape(row.Label)}[/]",
                RailRowKind.Empty => $"{Indent(row)}{Link(row, $"[dim]{Escape(row.Label)}[/]")}",
                RailRowKind.Character => Character(row),
                RailRowKind.Window => Window(row),
                _ => Escape(row.Label),
            });
        }

        return lines;
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
                    var unread = row.Unread > 0 ? $"[#00f5b7]{row.Unread}[/]" : string.Empty;
                    lines.Add(Link(row, $"[{Accent(row)}]{dot}[/]{name}{unread}"));
                    break;
            }
        }

        return lines;
    }

    private static string Character(RailRow row)
    {
        var marker = row.Active ? "[bold]▸[/]" : " ";
        var dot = row.Connected ? "●" : "○";
        var name = row.Active ? $"[bold]{Escape(row.Label)}[/]" : Escape(row.Label);
        var unread = row.Unread > 0 ? $"   [#00f5b7]{row.Unread}[/]" : string.Empty;
        return $"{Indent(row)}{Link(row, $"{marker} [{Accent(row)}]{dot}[/] {name}{unread}")}";
    }

    private static string Window(RailRow row)
    {
        var name = Escape(row.Label);
        var unsent = row.Unsent ? $" [#ffd700]{Glyphs.Draft}[/]" : string.Empty;
        var unread = row.Unread > 0 ? $" [#00f5b7]{row.Unread}[/]" : string.Empty;
        var pane = row.Closed ? "[dim]closed[/]" : $"[dim]{Escape(row.Pane ?? string.Empty)}[/]";
        return $"{Indent(row)}{Link(row, $"[dim]▪[/] {name}{unsent}{unread}   {pane}")}";
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
