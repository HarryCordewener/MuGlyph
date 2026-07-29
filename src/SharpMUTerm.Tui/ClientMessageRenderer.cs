using SharpMUTerm.Core.Diagnostics;
using static SharpMUTerm.Tui.MarkupText;

namespace SharpMUTerm.Tui;

/// <summary>
/// Renders the client message log — the status-line notices that dismiss themselves — as markup rows
/// for the ⌃P <c>Show client messages</c> viewer: <c>09:24:31  warn   ⌃B nothing to split …</c>, newest
/// last.
/// <para>
/// A timestamp and a severity per row, because the log is a debugging aid and an undated list of
/// sentences is not one. Pure, so what the viewer shows is unit-tested without a terminal.
/// </para>
/// </summary>
internal static class ClientMessageRenderer
{
    /// <summary>The <c>HH:mm:ss</c> stamp column plus the severity column, and the gaps around them.</summary>
    private const int GutterWidth = 8 + 2 + 5 + 2;

    /// <summary>What the viewer says when nothing has been recorded yet.</summary>
    internal const string Empty = "no client messages yet";

    public static IReadOnlyList<string> Render(IReadOnlyList<ClientMessage> entries, int width = 80)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var body = Math.Max(8, width - GutterWidth);
        var lines = new List<string>(entries.Count + 1);

        if (entries.Count == 0)
        {
            lines.Add($"[{ScreenPalette.Label}]{Empty}[/]");
            return lines;
        }

        foreach (var entry in entries)
        {
            var stamp = entry.At.ToLocalTime().ToString("HH:mm:ss");
            var label = Label(entry.Severity);
            lines.Add(
                $"[{ScreenPalette.Label}]{stamp}[/]  " +
                $"[{Colour(entry.Severity)}]{label.PadRight(5)}[/]  " +
                $"[{ScreenPalette.Value}]{Escape(Truncate(entry.Text, body))}[/]");
        }

        return lines;
    }

    /// <summary>The widest row the viewer would draw, so the window can be sized to its content.</summary>
    public static int MaxWidth(IReadOnlyList<ClientMessage> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var widest = Empty.Length;
        foreach (var entry in entries)
        {
            widest = Math.Max(widest, entry.Text.Length + GutterWidth);
        }

        return widest;
    }

    private static string Label(MessageSeverity severity) => severity switch
    {
        MessageSeverity.Error => "error",
        MessageSeverity.Warning => "warn",
        _ => "info",
    };

    private static string Colour(MessageSeverity severity) => severity switch
    {
        MessageSeverity.Error => ScreenPalette.Warn,
        MessageSeverity.Warning => "#e5c07b",
        _ => ScreenPalette.Muted,
    };

    /// <summary>Clips a message to the row, marking that it was clipped rather than lying about it.</summary>
    private static string Truncate(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(1, width - 1)] + "…";
}
