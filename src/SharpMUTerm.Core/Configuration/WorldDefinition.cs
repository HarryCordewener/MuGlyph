using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Transport;

namespace SharpMUTerm.Core.Configuration;

/// <summary>Which log formats a session writes.</summary>
public enum LogFormat
{
    None,
    Plain,
    Html,
    Both,
}

/// <summary>How a world's inbound text is interpreted for styling and interaction.</summary>
public enum ContentFormat
{
    /// <summary>Plain text with ANSI SGR colour (the default).</summary>
    Ansi,

    /// <summary>MXP markup (tags, SEND/A links) over ANSI.</summary>
    Mxp,

    /// <summary>Pueblo HTML-subset markup.</summary>
    Pueblo,
}

/// <summary>Emoji/emoticon substitution options.</summary>
public sealed class EmojiSettings
{
    public bool Enabled { get; set; }

    public bool Emoticons { get; set; } = true;

    public bool Shortcodes { get; set; } = true;
}

/// <summary>Per-character logging configuration.</summary>
public sealed class LoggingSettings
{
    public LogFormat Format { get; set; } = LogFormat.None;

    /// <summary>Directory for log files. Defaults to a per-session folder under the config dir.</summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Whether this character's panes come back with their previous session's content after a restart
    /// (<see cref="SharpMUTerm.Core.Text.RestoreLog"/>). On by default, and the per-character opt-out for
    /// somebody who does not want one world's text on disk between sessions.
    /// <para>
    /// It sits beside <see cref="Format"/> because F9 is where a character's "what of mine is written
    /// down" questions are answered, and it is a separate switch from it because the two settings are
    /// different things: a transcript is a file <em>you</em> keep, read and choose a format for, and this
    /// is a small bounded tail nothing but the client's own startup ever reads. Turning one off has never
    /// implied anything about the other.
    /// </para>
    /// <para>
    /// Clearing it stops the writing <em>and</em> drops whatever is already stored for that character's
    /// windows on the next launch — an opt-out that left the last session's text lying there would be
    /// answering a different question from the one it was asked.
    /// </para>
    /// </summary>
    public bool RestoreLog { get; set; } = true;
}

/// <summary>
/// A saved MU* world — i.e. a <b>server</b> (host/port/TLS/encoding + rendering options) that
/// holds zero or more <see cref="CharacterDefinition"/>s. A character is the connection unit;
/// automation lives in world-independent <see cref="TriggerSet"/>s on <see cref="AppConfiguration"/>.
/// A world with no characters is valid — it simply cannot connect.
/// </summary>
public sealed class WorldDefinition
{
    public string Name { get; set; } = "New World";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 4000;

    public bool UseTls { get; set; }

    public bool AllowInvalidCertificates { get; set; }

    /// <summary>Echo typed commands locally into the output window.</summary>
    public bool LocalEcho { get; set; } = true;

    /// <summary>
    /// This world's text encoding: <c>auto</c> (the default) to follow CHARSET negotiation, or a named
    /// encoding (e.g. <c>ISO-8859-1</c>) to <em>override</em> it.
    /// <para>
    /// An override is used for decoding whatever the server proposes, and is still offered at the head
    /// of the negotiation order so a cooperative server agrees rather than diverging. Under
    /// <c>auto</c> the session states the app-wide
    /// <see cref="AppConfiguration.CharsetOrder"/> and uses whatever the server settles on — or, when
    /// the server never implements RFC 2066, the head of that order.
    /// </para>
    /// </summary>
    public string Encoding { get; set; } = SharpMUTerm.Core.Telnet.TelnetSessionOptions.AutoEncodingName;

    /// <summary>Seconds between keepalive probes (NOP/telnet), or 0 to disable.</summary>
    public int KeepaliveSeconds { get; set; }

    /// <summary>How inbound text is parsed for styling and interactive links.</summary>
    public ContentFormat ContentFormat { get; set; } = ContentFormat.Ansi;

    /// <summary>Emoji/emoticon substitution for inbound text.</summary>
    public EmojiSettings Emoji { get; set; } = new();

    /// <summary>
    /// The world's accent colour, used to keep its windows traceable to their owner once they
    /// scatter across panes. Default resolves to a theme-derived accent.
    /// </summary>
    public TerminalColor Accent { get; set; } = TerminalColor.Default;

    /// <summary>The characters that can connect to this world.</summary>
    public List<CharacterDefinition> Characters { get; set; } = new();

    /// <summary>
    /// A deep copy of this world, characters and all. Same contract as
    /// <see cref="CharacterDefinition.Clone"/>: nothing mutable is shared with the original, so a
    /// duplicated world can be renamed and re-pointed without dragging its source along.
    /// </summary>
    public WorldDefinition Clone() => new()
    {
        Name = Name,
        Host = Host,
        Port = Port,
        UseTls = UseTls,
        AllowInvalidCertificates = AllowInvalidCertificates,
        LocalEcho = LocalEcho,
        Encoding = Encoding,
        KeepaliveSeconds = KeepaliveSeconds,
        ContentFormat = ContentFormat,
        Emoji = new EmojiSettings
        {
            Enabled = Emoji.Enabled,
            Emoticons = Emoji.Emoticons,
            Shortcodes = Emoji.Shortcodes,
        },
        Accent = Accent,
        Characters = Characters.Select(c => c.Clone()).ToList(),
    };

    /// <summary>Builds the transport-level options from this world.</summary>
    public ConnectionOptions ToConnectionOptions() => new()
    {
        Host = Host,
        Port = Port,
        UseTls = UseTls,
        AllowInvalidCertificates = AllowInvalidCertificates,
    };
}
