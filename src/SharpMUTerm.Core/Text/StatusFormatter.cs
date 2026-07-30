namespace SharpMUTerm.Core.Text;

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
}
