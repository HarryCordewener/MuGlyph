using MuClient.Core.Text;
using MuClient.Core.Workspaces;

namespace MuClient.Tui;

/// <summary>
/// Renders <see cref="RailModel"/> rows into markup lines for the connection rail: a header, worlds
/// with an accent spine, host lines, characters with a connected dot and active marker, and windows
/// with unread/unsent/pane detail. Pure so the rail layout is unit-testable.
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
                RailRowKind.Header => "[dim]┌ CONNECTIONS[/]",
                RailRowKind.World => $"[{Accent(row)}]▚[/] [bold]{Escape(row.Label)}[/]",
                RailRowKind.Host => $"{Indent(row)}[dim]{Escape(row.Label)}[/]",
                RailRowKind.Empty => $"{Indent(row)}[dim]{Escape(row.Label)}[/]",
                RailRowKind.Character => Character(row),
                RailRowKind.Window => Window(row),
                _ => Escape(row.Label),
            });
        }

        return lines;
    }

    private static string Character(RailRow row)
    {
        var marker = row.Active ? "[bold]▸[/]" : " ";
        var dot = row.Connected ? "●" : "○";
        var name = row.Active ? $"[bold]{Escape(row.Label)}[/]" : Escape(row.Label);
        var unread = row.Unread > 0 ? $"   [#00f5b7]{row.Unread}[/]" : string.Empty;
        return $"{Indent(row)}{marker} [{Accent(row)}]{dot}[/] {name}{unread}";
    }

    private static string Window(RailRow row)
    {
        var name = Escape(row.Label);
        var unsent = row.Unsent ? " [#ffd700]✎[/]" : string.Empty;
        var unread = row.Unread > 0 ? $" [#00f5b7]{row.Unread}[/]" : string.Empty;
        var pane = row.Closed ? "[dim]closed[/]" : $"[dim]{Escape(row.Pane ?? string.Empty)}[/]";
        return $"{Indent(row)}[dim]▪[/] {name}{unsent}{unread}   {pane}";
    }

    private static string Indent(RailRow row) => new(' ', row.Indent * 2);

    private static string Accent(RailRow row) =>
        row.Accent.Kind == TerminalColorKind.Rgb
            ? $"#{row.Accent.R:x2}{row.Accent.G:x2}{row.Accent.B:x2}"
            : DefaultAccent;

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
