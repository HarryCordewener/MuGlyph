using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SharpMUTerm.Core.Configuration;

/// <summary>
/// Loads and saves <see cref="AppConfiguration"/> as JSON, and joins it to the secrets that do not live in
/// it.
/// <para>
/// <b>This file is the shareable one.</b> Character passwords are <em>not</em> in it:
/// <see cref="CharacterDefinition.Password"/> is <c>[JsonIgnore]</c> and the document carries only
/// <see cref="CharacterDefinition.PasswordRef"/>, a GUID that means nothing on its own. The values live in
/// <see cref="SecretsStore"/>'s own owner-only file beside this one. That is the whole point of the split:
/// a config pasted into a bug report or committed to a dotfiles repository discloses hosts and character
/// names, which is what a config is for, and no credentials.
/// </para>
/// <para>
/// <see cref="Save"/> and <see cref="Load"/> are therefore the seam where the two files meet — the only
/// place that knows a reference stands for a password. Everything upstream of them reads and writes
/// <see cref="CharacterDefinition.Password"/> and never sees a GUID.
/// </para>
/// <para>
/// <c>config.json</c>'s own permissions are deliberately left alone. It holds no secrets, it is the file
/// this design exists to make shareable, and silently narrowing a file the user hand-edits and pastes would
/// buy nothing. Exactly one of the two files is <c>0600</c>, which is also the clearest available signal
/// about which one not to paste.
/// </para>
/// </summary>
public static class ConfigurationStore
{
    /// <summary>Shared serializer options: indented, string enums, and the colour converter.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new TerminalColorJsonConverter());
        options.Converters.Add(new RgbJsonConverter());
        return options;
    }

    /// <summary>The default configuration path under the user's profile.</summary>
    public static string DefaultPath
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }

            return Path.Combine(baseDir, "SharpMUTerm", "config.json");
        }
    }

    /// <summary>
    /// Loads the configuration and fills in each character's <see cref="CharacterDefinition.Password"/> from
    /// the secrets file beside it.
    /// <para>
    /// <paramref name="report"/> is called at most once, with a one-line description, when a secrets file
    /// exists and could not be fully understood. It is a callback rather than a logger because
    /// <c>SharpMUTerm.Core</c> owns no logging pipeline and because this runs before the entry point has
    /// built one — the caller stashes the line and logs it when diagnostics exist. Nothing on this path
    /// reports per character or per keystroke: a load happens once, and a client that nagged about a
    /// permission problem on every settings edit would be trained away in a day.
    /// </para>
    /// </summary>
    public static AppConfiguration Load(string path, Action<string>? report = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return new AppConfiguration();
        }

        var configuration = Deserialize(File.ReadAllText(path));
        ApplySecrets(configuration, SecretsStore.Read(SecretsStore.PathFor(path)), report);
        return configuration;
    }

    /// <summary>
    /// Resolves every <see cref="CharacterDefinition.PasswordRef"/> against the secrets that were read.
    /// <para>
    /// A reference with no row leaves <see cref="CharacterDefinition.Password"/> null — "no password", not an
    /// error — but the <em>reference is kept</em>. Dropping it would look tidier and would be the bug: a
    /// secrets file that was momentarily unreadable would come back with every character un-linked, and the
    /// next save would mint new GUIDs for whatever the user retyped and orphan the rows that were fine all
    /// along. Keeping the reference means a fixed file is picked back up on the next load with nothing lost.
    /// </para>
    /// </summary>
    private static void ApplySecrets(
        AppConfiguration configuration, SecretsStore.ReadResult read, Action<string>? report)
    {
        foreach (var character in configuration.Worlds.SelectMany(w => w.Characters))
        {
            character.Password = character.PasswordRef is { } id && read.Secrets.TryGetValue(id, out var password)
                ? password
                : null;
        }

        if (read.Problem is { } problem)
        {
            report?.Invoke(problem);
        }
    }

    public static AppConfiguration Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        // Parse to a DOM first so older files can be upgraded to the current schema in place.
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            return new AppConfiguration();
        }

        ConfigurationMigrator.Migrate(root);
        return root.Deserialize<AppConfiguration>(SerializerOptions) ?? new AppConfiguration();
    }

    public static string Serialize(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return JsonSerializer.Serialize(configuration, SerializerOptions);
    }

    /// <summary>
    /// Writes the configuration, and the secrets it references, as a pair.
    /// <para>
    /// The order is secrets first, then the config that points at them. If the second write fails, the
    /// secrets file holds a row nothing references, which resolves to nothing and is pruned by the next
    /// successful save. The reverse order would leave a config referencing a row that does not exist, which
    /// resolves to "no password" — recoverable, but it loses a credential the user had. Neither is a
    /// transaction; this is the ordering whose partial state costs less.
    /// </para>
    /// </summary>
    public static void Save(string path, AppConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(configuration);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var secrets = ReconcileSecrets(configuration);
        SecretsStore.Write(SecretsStore.PathFor(full), secrets);
        File.WriteAllText(full, Serialize(configuration));
    }

    /// <summary>
    /// Brings every <see cref="CharacterDefinition.PasswordRef"/> into line with the password the character
    /// actually holds, and hands back the rows to store.
    /// <para>
    /// It <b>mutates the configuration it is given</b>, which is deliberate and is why this is not a pure
    /// function: the GUID has to outlive the call. A save that allocated a reference locally would mint a
    /// fresh one every time, orphaning the previous row on every keystroke that commits. The object passed in
    /// is the live configuration the client is reading, so writing the reference onto it is also what makes
    /// the next save agree with this one.
    /// </para>
    /// <para>
    /// The returned map is built only from characters that exist <em>now</em>, and
    /// <see cref="SecretsStore.Write"/> replaces the file wholesale — so a deleted character's password is
    /// gone by construction rather than by a separate sweep. That is the answer to orphans: there is no
    /// lifecycle to get wrong, because the config is the only list of what should exist.
    /// </para>
    /// </summary>
    private static Dictionary<Guid, string> ReconcileSecrets(AppConfiguration configuration)
    {
        var secrets = new Dictionary<Guid, string>();
        foreach (var character in configuration.Worlds.SelectMany(w => w.Characters))
        {
            if (string.IsNullOrEmpty(character.Password))
            {
                // No password, no reference. A GUID pointing at nothing would say "this character has a
                // stored password" to anyone reading the config, and it would keep a row alive.
                character.PasswordRef = null;
                continue;
            }

            var id = character.PasswordRef ?? Guid.NewGuid();
            if (!secrets.TryAdd(id, character.Password))
            {
                // Two characters on one row — reachable through a hand-edited config, or a config copied
                // between machines — would mean editing one password silently changed the other, invisibly,
                // behind two masks. Give this one a row of its own instead. Clone() already declines to copy
                // the reference; this is the same rule enforced rather than intended.
                id = Guid.NewGuid();
                secrets[id] = character.Password;
            }

            character.PasswordRef = id;
        }

        return secrets;
    }
}
