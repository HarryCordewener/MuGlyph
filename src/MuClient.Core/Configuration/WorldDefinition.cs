using MuClient.Core.Text;
using MuClient.Core.Transport;

namespace MuClient.Core.Configuration;

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

    /// <summary>Builds the transport-level options from this world.</summary>
    public ConnectionOptions ToConnectionOptions() => new()
    {
        Host = Host,
        Port = Port,
        UseTls = UseTls,
        AllowInvalidCertificates = AllowInvalidCertificates,
    };
}
