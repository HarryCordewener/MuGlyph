namespace MuClient.Core.Configuration;

/// <summary>
/// A character on a <see cref="WorldDefinition"/> — the unit you actually connect *as*. A world
/// (server) may hold zero or more characters, and several can be connected at once; sessions are
/// keyed <c>world.character</c>. Automation is composed from the named <see cref="TriggerSets"/>.
/// </summary>
public sealed class CharacterDefinition
{
    public string Name { get; set; } = "New Character";

    /// <summary>Login password. Should be keychain-backed by the host; avoid persisting in plain JSON.</summary>
    public string? Password { get; set; }

    /// <summary>The login line to send. Defaults to <c>connect {Name} {Password}</c> when null.</summary>
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

    /// <summary>Builds the default login line when <see cref="ConnectString"/> is unset.</summary>
    public string ResolveConnectString() =>
        !string.IsNullOrWhiteSpace(ConnectString)
            ? ConnectString!
            : $"connect {Name}{(string.IsNullOrEmpty(Password) ? string.Empty : " " + Password)}";
}
