using System.Text;

namespace MuClient.Core.Text;

/// <summary>
/// Text meters for the status bar: a filled/empty bar (<c>████░░░░</c>) and a Unicode block
/// sparkline (<c>▁▃▅▇</c>). Pure and UI-agnostic so the status line is unit-testable.
/// </summary>
public static class Meters
{
    private const string Sparks = "▁▂▃▄▅▆▇█";

    /// <summary>Renders a bar of <paramref name="width"/> cells filled to <paramref name="value"/>/<paramref name="max"/>.</summary>
    public static string Bar(int value, int max, int width, char full = '█', char empty = '░')
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var fraction = max <= 0 ? 0 : Math.Clamp((double)value / max, 0, 1);
        var filled = (int)Math.Round(fraction * width, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, width);
        return new string(full, filled) + new string(empty, width - filled);
    }

    /// <summary>
    /// Renders a sparkline: each value maps to one of eight block glyphs, scaled across the series'
    /// own min…max. A flat series renders at mid height; an empty series yields an empty string.
    /// </summary>
    public static string Sparkline(IReadOnlyList<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return string.Empty;
        }

        var min = values.Min();
        var max = values.Max();
        var range = max - min;
        var sb = new StringBuilder(values.Count);
        foreach (var v in values)
        {
            var level = range == 0 ? Sparks.Length / 2 : (int)Math.Round((double)(v - min) / range * (Sparks.Length - 1));
            sb.Append(Sparks[Math.Clamp(level, 0, Sparks.Length - 1)]);
        }

        return sb.ToString();
    }
}
