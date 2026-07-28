using System.Text;
using MuClient.Core.Commands;

namespace MuClient.Tui;

/// <summary>
/// Renders the command surface (⌃P) results to markup lines, grouped GO TO / WORLD / TERMINAL /
/// LAYOUT with the selected row highlighted, per the design. The flattened row order matches
/// <see cref="Order"/> so ↑↓ navigation and dispatch stay in sync. Pure so it's unit-testable.
/// </summary>
internal static class CommandSurfaceRenderer
{
    private static readonly (CommandGroup Group, string Label)[] Groups =
    {
        (CommandGroup.GoTo, "GO TO"),
        (CommandGroup.World, "WORLD"),
        (CommandGroup.Terminal, "TERMINAL"),
        (CommandGroup.Layout, "LAYOUT"),
    };

    /// <summary>Flattens ranked results into display order: groups in fixed order, ranked within each.</summary>
    public static IReadOnlyList<CommandItem> Order(IReadOnlyList<RankedCommand> ranked)
    {
        var ordered = new List<CommandItem>(ranked.Count);
        foreach (var (group, _) in Groups)
        {
            foreach (var r in ranked)
            {
                if (r.Item.Group == group)
                {
                    ordered.Add(r.Item);
                }
            }
        }

        return ordered;
    }

    /// <summary>
    /// Builds the markup lines: a context header, then each group's header and its rows. The row at
    /// flattened index <paramref name="selected"/> is highlighted. When <paramref name="width"/> &gt; 0
    /// the selected row's highlight bar is padded to that visible width so it spans the full surface.
    /// </summary>
    public static List<string> Render(
        IReadOnlyList<RankedCommand> ranked, int selected, int total, string? context, int width = 0)
    {
        var lines = new List<string>
        {
            $"[dim]{Escape(ranked.Count.ToString())} of {Escape(total.ToString())}"
            + (string.IsNullOrEmpty(context) ? string.Empty : $"   acting on {Escape(context)}") + "[/]",
        };

        var flatIndex = 0;
        foreach (var (group, label) in Groups)
        {
            var inGroup = ranked.Where(r => r.Item.Group == group).ToList();
            if (inGroup.Count == 0)
            {
                continue;
            }

            lines.Add($"[dim]├ {label}[/]");
            foreach (var r in inGroup)
            {
                lines.Add(Row(r.Item, flatIndex == selected, width));
                flatIndex++;
            }
        }

        if (ranked.Count == 0)
        {
            lines.Add("[dim]  no matches[/]");
        }

        return lines;
    }

    /// <summary>The visible width of the widest rendered line — used to size the surface to its content.</summary>
    public static int MaxWidth(IReadOnlyList<string> lines)
    {
        var max = 0;
        foreach (var line in lines)
        {
            max = Math.Max(max, VisibleLength(line));
        }

        return max;
    }

    private static string Row(CommandItem item, bool selected, int width)
    {
        var title = Escape(item.Title);
        if (!selected)
        {
            var sub = item.Subtitle is null ? string.Empty : $"   [dim]{Escape(item.Subtitle)}[/]";
            return "   " + title + sub;
        }

        // Selected: one continuous accent bar across the whole row (title + subtitle), padded to the
        // surface width so the highlight reads as a full-width band rather than hugging the title.
        var subtitle = item.Subtitle is null ? string.Empty : $"   {Escape(item.Subtitle)}";
        var text = $" ▸ {title}{subtitle} ";
        var pad = width > VisibleLength(text) ? new string(' ', width - VisibleLength(text)) : string.Empty;
        return $"[#18181c on #00f5b7]{text}{pad}[/]";
    }

    /// <summary>Visible length of a markup string: strips <c>[…]</c> tags, counts <c>[[</c>/<c>]]</c> as one.</summary>
    private static int VisibleLength(string markup)
    {
        var length = 0;
        var i = 0;
        while (i < markup.Length)
        {
            var c = markup[i];
            if (c == '[')
            {
                if (i + 1 < markup.Length && markup[i + 1] == '[')
                {
                    length++;
                    i += 2;
                    continue;
                }

                var close = markup.IndexOf(']', i);
                if (close < 0)
                {
                    return length + (markup.Length - i);
                }

                i = close + 1;
                continue;
            }

            if (c == ']' && i + 1 < markup.Length && markup[i + 1] == ']')
            {
                length++;
                i += 2;
                continue;
            }

            length++;
            i++;
        }

        return length;
    }

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
