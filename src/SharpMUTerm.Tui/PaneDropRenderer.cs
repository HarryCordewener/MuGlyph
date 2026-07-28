using System.Text;
using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Tui;

/// <summary>
/// Paints a pane while a drag is in flight: every pane dims to its name, and the pane under the
/// pointer additionally fills in the drop zone the release would use — the 25% edge band for a split,
/// or a full outline for a central "add as a tab" drop — with the pending action spelled out across
/// its middle. Pure markup, so the preview is unit-testable without a terminal or a mouse.
/// </summary>
internal static class PaneDropRenderer
{
    /// <summary>The highlight for the live drop zone (the app's teal accent).</summary>
    internal const string ZoneColor = "#00f5b7";

    /// <summary>
    /// Renders one pane of the drag preview to <paramref name="height"/> markup rows of
    /// <paramref name="width"/> cells. <paramref name="edge"/> is only meaningful when
    /// <paramref name="hovered"/> is true: an edge splits, null adds a tab.
    /// </summary>
    internal static List<string> Render(
        string paneName,
        string label,
        int width,
        int height,
        bool hovered,
        Edge? edge,
        double edgeFraction = DropZones.DefaultEdgeFraction)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var text = CenteredRow(hovered ? label : paneName, width);
        var textRow = height / 2;
        var lines = new List<string>(height);

        for (var row = 0; row < height; row++)
        {
            var characters = row == textRow ? text : new string(' ', width);
            lines.Add(RenderRow(characters, row, width, height, hovered, edge, edgeFraction));
        }

        return lines;
    }

    /// <summary>
    /// True when the cell falls inside the region the drop would claim: the band along the targeted
    /// edge — roughly where the new pane will land, sized to the same fraction
    /// <see cref="DropZones"/> splits at — or the pane's outline when the drop adds a tab instead.
    /// It previews the <em>result</em>, not the set of points that resolve to this edge: near a corner
    /// those differ, because <see cref="DropZones"/> picks whichever edge is nearest.
    /// </summary>
    internal static bool InZone(
        int column,
        int row,
        int width,
        int height,
        Edge? edge,
        double edgeFraction = DropZones.DefaultEdgeFraction)
    {
        if (edge is null)
        {
            // A tab drop consumes the whole pane; outlining it says "here", without hiding the label.
            return column == 0 || column == width - 1 || row == 0 || row == height - 1;
        }

        var bandWidth = Band(width, edgeFraction);
        var bandHeight = Band(height, edgeFraction);

        return edge switch
        {
            Edge.Left => column < bandWidth,
            Edge.Right => column >= width - bandWidth,
            Edge.Top => row < bandHeight,
            _ => row >= height - bandHeight,
        };
    }

    /// <summary>The band thickness for a span, always at least one cell so it stays visible.</summary>
    internal static int Band(int span, double edgeFraction) =>
        Math.Clamp((int)Math.Round(span * edgeFraction, MidpointRounding.AwayFromZero), 1, Math.Max(1, span));

    private static string RenderRow(
        string characters,
        int row,
        int width,
        int height,
        bool hovered,
        Edge? edge,
        double edgeFraction)
    {
        var builder = new StringBuilder();
        var open = false;
        var highlighted = false;

        for (var column = 0; column < width; column++)
        {
            var zone = hovered && InZone(column, row, width, height, edge, edgeFraction);
            if (!open || zone != highlighted)
            {
                if (open)
                {
                    builder.Append("[/]");
                }

                builder.Append(zone ? $"[black on {ZoneColor}]" : "[dim]");
                open = true;
                highlighted = zone;
            }

            builder.Append(MarkupText.Escape(characters[column].ToString()));
        }

        if (open)
        {
            builder.Append("[/]");
        }

        return builder.ToString();
    }

    /// <summary>Centres text in a row of <paramref name="width"/> spaces, truncating when it won't fit.</summary>
    private static string CenteredRow(string text, int width)
    {
        if (text.Length > width)
        {
            text = width <= 1 ? text[..width] : text[..(width - 1)] + "…";
        }

        var left = (width - text.Length) / 2;
        return new string(' ', left) + text + new string(' ', width - left - text.Length);
    }
}
