namespace MuClient.Core.Text;

/// <summary>What activating an interactive span does.</summary>
public enum InteractionKind
{
    /// <summary>Send a command to the server (MXP <c>&lt;SEND&gt;</c>, Pueblo <c>xch_cmd</c>).</summary>
    SendCommand,

    /// <summary>Open a hyperlink (MXP <c>&lt;A HREF&gt;</c>, Pueblo <c>&lt;A&gt;</c>).</summary>
    Hyperlink,
}

/// <summary>
/// Makes a <see cref="StyledSpan"/> clickable: a command to send or a URL to open, plus an
/// optional hint (tooltip). Produced by the MXP and Pueblo parsers and realised by the UI
/// (mouse/keyboard activation). UI-agnostic.
/// </summary>
public sealed record SpanInteraction(
    InteractionKind Kind,
    string Target,
    string? Hint = null,
    bool PromptOnly = false)
{
    /// <summary>A clickable command. <paramref name="promptOnly"/> puts it on the input line instead of sending.</summary>
    public static SpanInteraction Command(string command, string? hint = null, bool promptOnly = false) =>
        new(InteractionKind.SendCommand, command, hint, promptOnly);

    /// <summary>A hyperlink.</summary>
    public static SpanInteraction Link(string url, string? hint = null) =>
        new(InteractionKind.Hyperlink, url, hint);
}
