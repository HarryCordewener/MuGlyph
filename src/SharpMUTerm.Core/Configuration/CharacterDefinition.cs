using System.Text.Json.Serialization;

namespace SharpMUTerm.Core.Configuration;

/// <summary>
/// A character on a <see cref="WorldDefinition"/> — the unit you actually connect *as*. A world
/// (server) may hold zero or more characters, and several can be connected at once; sessions are
/// keyed <c>world.character</c>. Automation is composed from the named <see cref="TriggerSets"/>.
/// </summary>
public sealed class CharacterDefinition
{
    public string Name { get; set; } = "New Character";

    /// <summary>
    /// Login password, held in memory for the session only. It is deliberately <b>never</b>
    /// serialized (<see cref="JsonIgnoreAttribute"/>) so it can't leak into plaintext
    /// <c>config.json</c>; a secure OS credential store (DPAPI/Keychain/libsecret) is the intended
    /// backing and is a follow-up. Until one lands, the F5 character form says exactly that — the
    /// password is remembered for this session and is not saved — because a field claiming a credential
    /// store that does not exist is worse than a field that admits it forgets.
    /// <para>
    /// It is typed into the form and joined to the login line by
    /// <see cref="ConnectStringTemplate.PasswordToken"/> at send time. That is the point of the token:
    /// the line can be persisted while the secret is not, so nobody has to choose between an auto-login
    /// that works and a config that holds their password in the clear.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string? Password { get; set; }

    /// <summary>
    /// The login line to send, as a template — <c>connect %CHARACTER% %PASSWORD%</c> by default (see
    /// <see cref="ConnectStringTemplate"/> for the token, escaping and empty-value rules). Null means
    /// "use the default", which is why no config migration was needed when the default stopped being
    /// hand-built and became a template: an existing character with <c>connectString: null</c> resolves
    /// to the very same line it always did, and one carrying its own line keeps it.
    /// </summary>
    public string? ConnectString { get; set; }

    /// <summary>Send the connect string automatically on connect.</summary>
    public bool AutoLogin { get; set; }

    /// <summary>Semicolon-separated commands sent after connecting.</summary>
    public string? OnConnect { get; set; }

    /// <summary>Semicolon-separated commands sent (or run locally) on disconnect.</summary>
    public string? OnDisconnect { get; set; }

    /// <summary>Names of the <see cref="TriggerSet"/>s that apply to this character.</summary>
    public List<string> TriggerSets { get; set; } = new();

    /// <summary>Logging is configured per character.</summary>
    public LoggingSettings Logging { get; set; } = new();

    /// <summary>
    /// A deep copy of this character — every mutable part copied, not shared. The F5 screen's
    /// <c>duplicate</c> button is the caller: a copy that aliased <see cref="TriggerSets"/> or
    /// <see cref="Logging"/> would look right on screen and then follow every later edit of the
    /// original around, which is the sort of bug that only shows up once someone has re-pointed one
    /// copy's log directory and lost the other's.
    /// <para>
    /// <see cref="Password"/> is carried over deliberately: it is <c>[JsonIgnore]</c> session state,
    /// and a duplicate of a logged-in character that silently forgot its password would be a worse
    /// surprise than one that kept it. Nothing here reaches disk.
    /// </para>
    /// </summary>
    public CharacterDefinition Clone() => new()
    {
        Name = Name,
        Password = Password,
        ConnectString = ConnectString,
        AutoLogin = AutoLogin,
        OnConnect = OnConnect,
        OnDisconnect = OnDisconnect,
        TriggerSets = new List<string>(TriggerSets),
        Logging = new LoggingSettings { Format = Logging.Format, Directory = Logging.Directory },
    };

    /// <summary>
    /// The login line to send: this character's <see cref="ConnectString"/> — or
    /// <see cref="ConnectStringTemplate.Default"/> when it has none — with its tokens substituted. The
    /// secret is joined to the line here, at the last possible moment, and nowhere else.
    /// </summary>
    public string ResolveConnectString() => ConnectStringTemplate.Resolve(ConnectString, Name, Password);
}
