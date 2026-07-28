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
    /// flattened index <paramref name="selected"/> is highlighted.
    /// </summary>
    public static List<string> Render(IReadOnlyList<RankedCommand> ranked, int selected, int total, string? context)
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
                lines.Add(Row(r.Item, flatIndex == selected));
                flatIndex++;
            }
        }

        if (ranked.Count == 0)
        {
            lines.Add("[dim]  no matches[/]");
        }

        return lines;
    }

    private static string Row(CommandItem item, bool selected)
    {
        var sb = new StringBuilder();
        var title = Escape(item.Title);
        var subtitle = item.Subtitle is null ? string.Empty : $"   [dim]{Escape(item.Subtitle)}[/]";
        if (selected)
        {
            sb.Append("[#18181c on #00f5b7] ▸ ").Append(title).Append(" [/]").Append(subtitle);
        }
        else
        {
            sb.Append("   ").Append(title).Append(subtitle);
        }

        return sb.ToString();
    }

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
