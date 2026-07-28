namespace SharpMUTerm.Core.Configuration;

/// <summary>
/// Application-wide text rendering preferences — the settings the F7 "Text &amp; ANSI" screen edits.
/// These are global rather than per world: they describe how *this terminal* draws what any world
/// sends, so a world's own <see cref="WorldDefinition.Encoding"/> and
/// <see cref="WorldDefinition.ContentFormat"/> stay where they are.
/// </summary>
public sealed class TextSettings
{
    /// <summary>Discard inbound SGR colour and render every line in the theme's default style.</summary>
    public bool StripIncomingColour { get; set; }

    /// <summary>Honour the blink attribute rather than dropping it.</summary>
    public bool AllowBlink { get; set; }

    /// <summary>Underline MXP/Pueblo/web links so they read as clickable.</summary>
    public bool UnderlineHyperlinks { get; set; } = true;

    /// <summary>Substitute emoji for shortcodes and emoticons in inbound text.</summary>
    public bool EmojiSubstitution { get; set; } = true;

    /// <summary>How East Asian ambiguous-width characters are measured: <c>narrow</c> or <c>wide</c>.</summary>
    public string AmbiguousWidth { get; set; } = "narrow";
}

/// <summary>
/// Application-wide input and spellcheck preferences — the settings the F8 "Input &amp; spellcheck"
/// screen edits.
/// </summary>
public sealed class InputSettings
{
    /// <summary>Echo typed commands into the output window.</summary>
    public bool LocalEcho { get; set; } = true;

    /// <summary>Keep an unsent draft per tab so switching windows doesn't lose typing.</summary>
    public bool KeepDrafts { get; set; } = true;

    /// <summary>The key that inserts a newline instead of sending (e.g. <c>Shift+Enter</c>).</summary>
    public string NewlineKey { get; set; } = "Shift+Enter";

    /// <summary>Spell-check the input line as it is typed.</summary>
    public bool CheckSpelling { get; set; } = true;

    /// <summary>The dictionary spellcheck loads (e.g. <c>en_US</c>).</summary>
    public string Dictionary { get; set; } = "en_US";
}
