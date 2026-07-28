namespace MuClient.Core.Text;

/// <summary>
/// Builds the design's input-prompt string and its right-hand gutter (destination window + other
/// windows holding drafts + character count), both bound to the focused character. Pure and
/// UI-agnostic; the view colours/positions the pieces.
/// </summary>
public static class StatusFormatter
{
    /// <summary>The prompt label, e.g. <c>Corvid@aetherfall ›</c>. Falls back gracefully when disconnected.</summary>
    public static string CharacterPrompt(string? character, string? world)
    {
        if (string.IsNullOrEmpty(character))
        {
            return "› ";
        }

        return string.IsNullOrEmpty(world) ? $"{character} › " : $"{character}@{world} › ";
    }

    /// <summary>
    /// The input gutter: the destination window (<c>→ main</c>), a <c>✎</c> list of other windows
    /// holding drafts, and the current character count. Segments are omitted when empty.
    /// </summary>
    public static string InputGutter(string? destination, IReadOnlyList<string> draftWindows, int charCount)
    {
        ArgumentNullException.ThrowIfNull(draftWindows);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(destination))
        {
            parts.Add($"→ {destination}");
        }

        if (draftWindows.Count > 0)
        {
            parts.Add($"✎ {string.Join(' ', draftWindows)}");
        }

        parts.Add($"{charCount}");
        return string.Join("  ", parts);
    }
}
