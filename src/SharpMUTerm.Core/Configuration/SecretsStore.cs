using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpMUTerm.Core.Configuration;

/// <summary>
/// The second file: <c>secrets.json</c>, a flat map of GUID to password, sitting beside
/// <c>config.json</c> and readable only by its owner.
/// <para>
/// <b>Why two files.</b> Character passwords have to be saved — a client that forgets them is a client
/// whose users type credentials into the command line, where they are echoed, transcribed and offered to
/// history. But <c>config.json</c> is the file people <em>share</em>: it goes into bug reports, help
/// channels, screenshots and dotfiles repositories, and a secret in it leaks the first time anyone asks
/// "what does your config look like". So the secret is not in it. Config carries
/// <see cref="CharacterDefinition.PasswordRef"/>, a GUID that means nothing on its own, and the value it
/// stands for lives here. Pasting a config now leaks a hostname and a character name, which is what a
/// config is for.
/// </para>
/// <para>
/// <b>What this is not.</b> It is not encryption, and it does not pretend to be: the passwords are
/// plaintext in a JSON file, exactly as hand-editable as the config, and anyone who can read the file can
/// read them. What the split buys is that accidental disclosure and deliberate sharing stop being the same
/// action — which is the failure that actually happens to people. What the file mode buys is that "anyone
/// who can read the file" is one account. Reversible obfuscation would buy neither and would cost a
/// permanent migration burden plus a field that looks protected and is not. An OS credential store
/// (DPAPI/Keychain/libsecret) is the thing that would make these secrets at rest; it remains a legitimate
/// future option and nothing here claims it exists.
/// </para>
/// <para>
/// <b>Everything degrades to "no password".</b> A missing file, an unparseable one, a key that is not a
/// GUID, a reference with no row: all of them mean the character has no stored password, the client starts,
/// and <c>%PASSWORD%</c> resolves to nothing (which <see cref="ConnectStringTemplate"/> already handles by
/// dropping one adjacent space). None of it is an error that blocks a login, because a client that refuses
/// to connect over a permission bit on a convenience file is worse than one that asks for the password
/// again.
/// </para>
/// <para>
/// <b>No file until there is something to put in it.</b> A user who has saved no passwords has no
/// <c>secrets.json</c> at all — the same principle as the scrollback spill only creating its cache on the
/// first eviction. An unexplained file next to your config invites the question "what is in there, and can
/// I delete it".
/// </para>
/// </summary>
public static class SecretsStore
{
    /// <summary>
    /// The file name, beside the configuration. It is deliberately the name <c>.gitignore</c> already
    /// ignores, so a user who keeps their config in a dotfiles repository does not have to notice this
    /// design to be protected by it.
    /// </summary>
    public const string FileName = "secrets.json";

    /// <summary>
    /// The mode the secrets file is written with on Unix: read and write for the owner, nothing for
    /// anyone else. <c>0600</c>.
    /// <para>
    /// <c>config.json</c> is deliberately <em>not</em> narrowed. It holds no secrets now, it is the file
    /// this design exists to make shareable, and silently changing the mode of a file a user hand-edits and
    /// pastes buys nothing. One file being owner-only is also the clearest possible signal about which of
    /// the two not to paste.
    /// </para>
    /// </summary>
    public const UnixFileMode OwnerOnlyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Where the secrets file sits for a given configuration path: right beside it.</summary>
    public static string PathFor(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configurationPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(configurationPath));
        return Path.Combine(directory ?? string.Empty, FileName);
    }

    /// <summary>
    /// What a read produced. <paramref name="Readable"/> distinguishes "there is nothing stored" (a missing
    /// file, which is the normal state for most users) from "there is something stored and this code could
    /// not read it" — the second is worth telling the user about exactly once, and the first is not worth
    /// mentioning at all.
    /// </summary>
    /// <param name="Secrets">Every GUID-keyed entry that parsed, possibly none.</param>
    /// <param name="Readable">False only when a file exists and could not be understood.</param>
    /// <param name="Problem">A one-line description for the diagnostics log, or null when all was well.</param>
    public readonly record struct ReadResult(
        IReadOnlyDictionary<Guid, string> Secrets,
        bool Readable,
        string? Problem);

    private static readonly ReadResult Empty =
        new(new Dictionary<Guid, string>(), Readable: true, Problem: null);

    /// <summary>
    /// Reads the secrets file. Never throws: a missing file is an empty result, and anything else that goes
    /// wrong is an empty result carrying a problem to report.
    /// <para>
    /// Entries whose key is not a GUID are skipped rather than rejecting the whole file, because the file is
    /// hand-editable and one bad line should not cost the other nine. They are counted into
    /// <see cref="ReadResult.Problem"/> so a typo is not silent — but the read still <em>succeeds</em>, and
    /// that distinction is what stops a single stray key being treated as an unreadable file and moved
    /// aside.
    /// </para>
    /// </summary>
    public static ReadResult Read(string secretsPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretsPath);
        if (!File.Exists(secretsPath))
        {
            return Empty;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(secretsPath)) is not JsonObject root)
            {
                return Unreadable("the secrets file is not a JSON object");
            }

            var secrets = new Dictionary<Guid, string>();
            var skipped = 0;
            foreach (var (key, value) in root)
            {
                if (Guid.TryParse(key, out var id) && value is JsonValue text &&
                    text.TryGetValue<string>(out var password))
                {
                    secrets[id] = password;
                }
                else
                {
                    skipped++;
                }
            }

            return new ReadResult(
                secrets,
                Readable: true,
                Problem: skipped == 0
                    ? null
                    : $"{skipped} entr{(skipped == 1 ? "y" : "ies")} in the secrets file were not " +
                      "GUID-to-text pairs and were ignored");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return Unreadable($"the secrets file could not be read ({e.GetType().Name})");
        }
    }

    private static ReadResult Unreadable(string problem) =>
        new(new Dictionary<Guid, string>(), Readable: false, Problem: problem);

    /// <summary>
    /// Replaces the secrets file with exactly <paramref name="secrets"/> — so an entry no longer referenced
    /// by any character is gone, which is how a deleted character's password stops existing.
    /// <para>
    /// Two rules protect the user from that wholesale replacement. An <b>empty</b> map deletes the file
    /// rather than writing <c>{}</c>. And a file that exists but could not be <em>read</em> is moved aside
    /// (<c>.unreadable</c>) before anything replaces it: the caller cannot have loaded those passwords, so
    /// it is about to write a map that does not contain them, and destroying bytes nobody has parsed would
    /// turn a recoverable typo into every password lost.
    /// </para>
    /// </summary>
    public static void Write(string secretsPath, IReadOnlyDictionary<Guid, string> secrets)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretsPath);
        ArgumentNullException.ThrowIfNull(secrets);

        if (File.Exists(secretsPath) && !Read(secretsPath).Readable)
        {
            MoveAside(secretsPath);
        }

        if (secrets.Count == 0)
        {
            Delete(secretsPath);
            return;
        }

        // Ordinal ordering so the file is stable between saves: a map whose rows shuffle makes every save a
        // diff, which matters to anyone keeping their config directory in version control.
        var document = new JsonObject();
        foreach (var (id, password) in secrets.OrderBy(e => e.Key.ToString("D"), StringComparer.Ordinal))
        {
            document[id.ToString("D")] = password;
        }

        var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(secretsPath))!);
        RestrictToOwner(secretsPath);
        File.WriteAllText(secretsPath, json);
    }

    /// <summary>
    /// Creates <paramref name="path"/> if it does not exist and narrows it to <see cref="OwnerOnlyMode"/>,
    /// returning whether the file is now owner-only. False means the mode could not be applied — Windows, or
    /// a filesystem that cannot express one — not that anything failed in a way a caller should react to.
    /// <para>
    /// A missing file is created through <see cref="FileStreamOptions.UnixCreateMode"/> rather than created
    /// and then chmodded, so the mode is set by the <c>open</c> that makes the inode and the umask never
    /// gets to widen it. Narrowing before the write rather than after is the same point: a chmod that
    /// follows would leave a window in which a world-readable file already held passwords.
    /// </para>
    /// <para>
    /// An <b>existing</b> file is narrowed in place, silently. That is deliberate: the condition is already
    /// fixed by the time anyone could be told, refusing to write would lose the user's passwords over a
    /// permission bit, and a notice about a thing needing no action is a nag. It is written down in
    /// <c>docs/design/README.md</c> instead.
    /// </para>
    /// <para>
    /// <b>Windows takes no equivalent action, on purpose.</b> The closest analogue would be writing an
    /// explicit DACL, and the file already inherits a user-only ACL from <c>%APPDATA%</c>, which Windows
    /// creates restricted to the profile's owner. Hand-rolling a DACL would replace something correct with
    /// untestable code — nothing in this repository's CI runs on Windows — whose failure mode is locking a
    /// user out of their own credentials. If a Windows build ever needs more than the inherited ACL, that is
    /// a change with a Windows machine attached to it.
    /// </para>
    /// </summary>
    public static bool RestrictToOwner(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                using var created = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = OwnerOnlyMode,
                });

                return true;
            }

            if (File.GetUnixFileMode(path) != OwnerOnlyMode)
            {
                File.SetUnixFileMode(path, OwnerOnlyMode);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Losing the passwords because a filesystem cannot express a mode would be the worse failure of
            // the two. The caller carries on and writes the file.
            return false;
        }
    }

    /// <summary>Where an unreadable secrets file is parked so its bytes survive being replaced.</summary>
    internal const string AsideSuffix = ".unreadable";

    private static void MoveAside(string secretsPath)
    {
        try
        {
            File.Move(secretsPath, secretsPath + AsideSuffix, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort. The write below is what the user asked for and must still happen.
        }
    }

    private static void Delete(string secretsPath)
    {
        try
        {
            File.Delete(secretsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A stale file that cannot be removed still resolves to nothing once no reference points at it.
        }
    }
}
