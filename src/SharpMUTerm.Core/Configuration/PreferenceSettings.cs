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
/// exist. There is still no <c>NewlineKey</c>, but for the opposite reason to the one that removed it:
/// the command line wraps and grows now, so a newline <em>can</em> be typed — on one fixed chord this
/// host can actually deliver. Making it configurable would offer a field whose interesting answers
/// (Shift+⏎, Ctrl+⏎) no Unix terminal reports distinctly, which is the same empty promise again.
/// </para>
/// </summary>
public sealed class InputSettings
{
    /// <summary>The shortest a command line may be, and the floor both row settings clamp to.</summary>
    public const int MinRows = 1;

    /// <summary>The tallest a command line may be, whatever the configuration asks for.</summary>
    public const int MaxRowCeiling = 20;

    /// <summary>Echo typed commands into the output window.</summary>
    public bool LocalEcho { get; set; } = true;

    /// <summary>Keep an unsent draft per tab so switching windows doesn't lose typing.</summary>
    public bool KeepDrafts { get; set; } = true;

    /// <summary>
    /// Keep hand-typed login lines out of command history — <c>connect &lt;name&gt; &lt;password&gt;</c>
    /// and the other verbs <see cref="Input.HistorySecrets"/> lists. On by default, and the analogue of
    /// bash's <c>HISTIGNORE</c>: history is browsable and searchable (⌃R), so a password recorded in it is
    /// a password on display. Turning it off means those lines are recalled like any other; nothing is
    /// written to disk either way.
    /// </summary>
    public bool ExcludeCredentials { get; set; } = true;

    /// <summary>
    /// How many lines tall the command line is before anything is typed into it. Three by default, so
    /// a wrapped line has somewhere to wrap to without the input resizing on the first keystroke.
    /// </summary>
    public int Rows { get; set; } = 3;

    /// <summary>
    /// The most lines the command line grows to as it wraps. Past this it scrolls inside itself rather
    /// than taking any more of the output window. Clamped to no less than <see cref="Rows"/>.
    /// </summary>
    public int MaxRows { get; set; } = 8;

    /// <summary>
    /// Whether a window starts with its second command line shown. The bar is toggled per window
    /// (⌃B i) — this is only what a window that has never been told otherwise does.
    /// </summary>
    public bool SecondBar { get; set; }
}
