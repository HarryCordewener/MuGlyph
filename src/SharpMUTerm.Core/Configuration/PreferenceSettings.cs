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

    /// <summary>
    /// Substitute emoji for shortcodes and emoticons in inbound text. The app-wide off switch over
    /// <see cref="WorldDefinition.Emoji"/>, which is where a world opts in and says which
    /// substitutions it wants — see <c>WorldSession.ApplyEmoji</c>.
    /// </summary>
    public bool EmojiSubstitution { get; set; } = true;

    // There is deliberately no "ambiguous width" here. It was a setting with nothing behind it: every
    // column measurement in this app is SharpConsoleUI's (Helpers/UnicodeWidth.cs), which asks the
    // Wcwidth tables and offers no East-Asian-ambiguous policy to set. Honouring it needs an upstream
    // seam, and until there is one, the honest state is no control rather than a stored string.
}

/// <summary>
/// Application-wide input preferences — the settings the F8 "Input" screen edits.
/// <para>
/// Spellcheck used to live here (<c>CheckSpelling</c>, <c>Dictionary</c>) and was removed with its
/// checkboxes: there is no speller in this client, so the two values described a feature that did not
/// exist. So did <c>NewlineKey</c> — the command line is a single-line
/// <c>PromptControl</c>, and no chord can put a newline into a control that has no second row.
/// </para>
/// </summary>
public sealed class InputSettings
{
    /// <summary>Echo typed commands into the output window.</summary>
    public bool LocalEcho { get; set; } = true;

    /// <summary>Keep an unsent draft per tab so switching windows doesn't lose typing.</summary>
    public bool KeepDrafts { get; set; } = true;
}
