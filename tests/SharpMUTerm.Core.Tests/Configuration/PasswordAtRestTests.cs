using SharpMUTerm.Core.Configuration;

namespace SharpMUTerm.Core.Tests.Configuration;

/// <summary>
/// Passwords on disk: that they are saved, that <c>config.json</c> does not contain them, and that every way
/// the secrets file can be missing or broken still lets a client start and log in.
/// <para>
/// <b>This file replaces assertions that inverted twice in one day</b>, so it is worth stating what is now
/// pinned and why. The original design had <see cref="CharacterDefinition.Password"/> as <c>[JsonIgnore]</c>
/// session state and pinned its <em>absence</em> from the JSON — which was a real property, but it bought
/// that property by making the client forget passwords, and users answer that by baking the secret into
/// <see cref="CharacterDefinition.ConnectString"/>, which was serialized anyway. The second design saved it
/// as plaintext in <c>config.json</c> and pinned its presence — honest, and it leaks the first time anyone
/// pastes their config, which is a thing that has already happened.
/// </para>
/// <para>
/// The design under test keeps both properties at once, and both are pinned here:
/// <see cref="ConfigurationStoreDoesNotWriteThePasswordIntoTheConfigDocument"/> is the absence, and
/// <see cref="APasswordSurvivesSaveAndReloadThroughThePairOfFiles"/> is the persistence. Neither is a
/// weakening of anything: the config claim is the *stronger* of the two absence claims, because it holds
/// while the password is genuinely saved rather than because nothing was saved.
/// </para>
/// <para>
/// Everything here goes through <see cref="ConfigurationStore.Save"/> and
/// <see cref="ConfigurationStore.Load"/> against real files in a throwaway directory. Nothing in this file
/// touches <see cref="ConfigurationStore.DefaultPath"/> — the developer's own configuration is not a
/// fixture.
/// </para>
/// </summary>
public class PasswordAtRestTests
{
    /// <summary>A throwaway directory that is removed however the test ends.</summary>
    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"smuterm-config-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string Directory { get; }

        /// <summary>The config path inside it — one level deeper, so <c>Save</c> has a folder to create.</summary>
        public string ConfigPath => Path.Combine(Directory, "SharpMUTerm", "config.json");

        /// <summary>Where the secrets file must end up: beside the config, never anywhere else.</summary>
        public string SecretsPath => Path.Combine(Directory, "SharpMUTerm", SecretsStore.FileName);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (Exception)
            {
                // Nothing a test should fail over.
            }
        }
    }

    /// <summary>
    /// One character with no password. Named rather than written as <c>WithPasswords(null)</c>, which the
    /// compiler reads as a null <em>array</em> rather than a one-element array holding null.
    /// </summary>
    private static readonly string?[] NoPassword = { null };

    private static AppConfiguration WithPasswords(params string?[] passwords)
    {
        var world = new WorldDefinition { Name = "Aetherfall", Host = "aetherfall.mux", Port = 4201 };
        for (var i = 0; i < passwords.Length; i++)
        {
            world.Characters.Add(new CharacterDefinition
            {
                Name = i == 0 ? "Corvid" : $"Corvid{i}",
                Password = passwords[i],
                AutoLogin = true,
            });
        }

        return new AppConfiguration { Worlds = { world } };
    }

    private static CharacterDefinition Reload(string path, Action<string>? report = null) =>
        ConfigurationStore.Load(path, report).Worlds[0].Characters[0];

    // ---- config.json carries no secret ---------------------------------------------------------------

    /// <summary>
    /// <b>The property the whole design exists for.</b> Save a config with a password set, read the config
    /// file's raw text, and the password is not in it — anywhere, in any form. What is in it is a GUID, which
    /// discloses that a password exists and nothing else.
    /// <para>
    /// Asserted over the file's own bytes rather than over the model, because the leak this prevents is
    /// somebody reading or pasting the file. Awkward values are included so the check cannot pass by the
    /// secret merely being JSON-escaped into an unrecognisable form: each is searched for both as itself and
    /// as the text the serializer would have written.
    /// </para>
    /// </summary>
    [Test]
    [Arguments("zvxq-canary-71")]
    [Arguments("hunter2")]
    [Arguments("quote\"and\\slash")]
    [Arguments("пароль-🔑")]
    public async Task ConfigurationStoreDoesNotWriteThePasswordIntoTheConfigDocument(string password)
    {
        using var temp = new TempRoot();

        var config = WithPasswords(password);
        ConfigurationStore.Save(temp.ConfigPath, config);

        var document = File.ReadAllText(temp.ConfigPath);
        await Assert.That(document).DoesNotContain(password);

        // And not as the serializer would have escaped it either — `\"` and `\\` survive a naive search of
        // the raw literal, and non-ASCII is escaped to \uXXXX by the default encoder.
        var escaped = System.Text.Json.JsonSerializer.Serialize(password).Trim('"');
        await Assert.That(document).DoesNotContain(escaped);

        // Positively: the reference is there, so the absence above is not "nothing was saved".
        var reference = config.Worlds[0].Characters[0].PasswordRef;
        await Assert.That(reference).IsNotNull();
        await Assert.That(document).Contains(reference!.Value.ToString("D"));
        await Assert.That(File.ReadAllText(temp.SecretsPath)).Contains(reference.Value.ToString("D"));
    }

    /// <summary>
    /// The same property stated about <see cref="ConfigurationStore.Serialize"/> itself, which is the API a
    /// future refactor is most likely to reach for without noticing there is a second file. A document
    /// produced from a config holding a password contains no password, full stop — there is no path through
    /// the serializer that emits one, because the property it would come from is <c>[JsonIgnore]</c>.
    /// </summary>
    [Test]
    public async Task SerializingAConfigInMemoryAlsoEmitsNoPassword()
    {
        var json = ConfigurationStore.Serialize(WithPasswords("zvxq-canary-71"));

        await Assert.That(json).DoesNotContain("zvxq-canary-71");
        await Assert.That(json).DoesNotContain("password\":");
    }

    // ---- it survives, through the pair ---------------------------------------------------------------

    /// <summary>
    /// The property the feature is for: type a password, restart, still logged in. Save then Load through
    /// real files, and the value that comes back is the value that went in — carried by the reference in the
    /// config and the row in the secrets file, neither of which is any use without the other.
    /// </summary>
    [Test]
    public async Task APasswordSurvivesSaveAndReloadThroughThePairOfFiles()
    {
        using var temp = new TempRoot();
        const string secret = "zvxq-at-rest-71";

        ConfigurationStore.Save(temp.ConfigPath, WithPasswords(secret));

        // Both files exist, and the secret is in exactly one of them.
        await Assert.That(File.Exists(temp.SecretsPath)).IsTrue();
        await Assert.That(File.ReadAllText(temp.SecretsPath)).Contains(secret);
        await Assert.That(File.ReadAllText(temp.ConfigPath)).DoesNotContain(secret);

        await Assert.That(Reload(temp.ConfigPath).Password).IsEqualTo(secret);
    }

    /// <summary>
    /// The reference is <b>stable</b> across saves. A save that minted a fresh GUID each time would still
    /// round-trip — and would orphan a row on every keystroke that commits, growing the secrets file without
    /// bound. This is the assertion that catches that, and it is why <c>ReconcileSecrets</c> writes the
    /// reference back onto the live configuration rather than allocating one locally.
    /// </summary>
    [Test]
    public async Task TheReferenceIsAllocatedOnceAndReusedByEveryLaterSave()
    {
        using var temp = new TempRoot();
        var config = WithPasswords("zvxq-stable-71");

        ConfigurationStore.Save(temp.ConfigPath, config);
        var first = config.Worlds[0].Characters[0].PasswordRef;

        ConfigurationStore.Save(temp.ConfigPath, config);
        config.Worlds[0].Characters[0].Password = "zvxq-stable-71-changed";
        ConfigurationStore.Save(temp.ConfigPath, config);

        await Assert.That(config.Worlds[0].Characters[0].PasswordRef).IsEqualTo(first);

        // One row, not three: the file holds the current value under the original key.
        var read = SecretsStore.Read(temp.SecretsPath);
        await Assert.That(read.Secrets.Count).IsEqualTo(1);
        await Assert.That(read.Secrets[first!.Value]).IsEqualTo("zvxq-stable-71-changed");
    }

    /// <summary>
    /// Awkward values round-trip byte-for-byte. A password is the one string in this config nobody can
    /// eyeball for correctness — it is drawn as dots — so a value quietly mangled in transit fails at the
    /// server with the form on screen showing something that looks fine.
    /// <para>
    /// The interesting ones and why: <c>%</c> and <c>%PASSWORD%</c> because the connect line is a token
    /// template and a stored value must never be re-read as syntax; quotes, backslashes and braces because
    /// JSON escapes them; leading and trailing spaces because this is the one field on these screens that is
    /// deliberately <em>not</em> trimmed; a newline because the model can hold one even though the F5 field
    /// refuses control characters, so the store must not be the thing that breaks; combining marks and
    /// astral-plane characters because a password is text and not a display string.
    /// </para>
    /// </summary>
    [Test]
    [Arguments(" ")]
    [Arguments("   ")]
    [Arguments("\t")]
    [Arguments(" padded ")]
    [Arguments("%")]
    [Arguments("%%")]
    [Arguments("%PASSWORD%")]
    [Arguments("%CHARACTER%")]
    [Arguments("50%off")]
    [Arguments("quote\"and'apostrophe")]
    [Arguments("back\\slash")]
    [Arguments("brace{}bracket[]")]
    [Arguments("<tag>&amp;</tag>")]
    [Arguments("line\nbreak")]
    [Arguments("carriage\r\nreturn")]
    [Arguments("Ünïcødé-pässwörd")]
    [Arguments("пароль-密碼-🔑")]
    [Arguments("écombining")]
    public async Task AnAwkwardPasswordRoundTripsUnchanged(string password)
    {
        using var temp = new TempRoot();

        ConfigurationStore.Save(temp.ConfigPath, WithPasswords(password));

        await Assert.That(Reload(temp.ConfigPath).Password).IsEqualTo(password);

        // The config document holds no password — but only checked for values that could be recognised in
        // one. A password of a single space is a substring of any indented JSON, so asserting its absence
        // would fail on a document that leaked nothing; the absence property is pinned properly, against
        // distinctive values, in ConfigurationStoreDoesNotWriteThePasswordIntoTheConfigDocument.
        if (password.Trim().Length > 0)
        {
            await Assert.That(File.ReadAllText(temp.ConfigPath)).DoesNotContain(password);
        }
    }

    /// <summary>
    /// 512 characters, as its own test rather than an <c>[Arguments]</c> row so the value can be generated.
    /// </summary>
    [Test]
    public async Task AVeryLongPasswordRoundTripsUnchanged()
    {
        using var temp = new TempRoot();
        var password = string.Concat(Enumerable.Range(0, 512).Select(i => (char)('!' + (i % 90))));

        ConfigurationStore.Save(temp.ConfigPath, WithPasswords(password));

        await Assert.That(Reload(temp.ConfigPath).Password).IsEqualTo(password);
    }

    // ---- no password means no file and no reference --------------------------------------------------

    /// <summary>
    /// A user with no saved passwords has no secrets file at all — not an empty one — and no reference in
    /// their config. Same principle as the scrollback spill only creating its cache on the first eviction:
    /// an unexplained file next to your config invites "what is in there, and can I delete it".
    /// </summary>
    [Test]
    public async Task NoPasswordMeansNoReferenceAndNoSecretsFile()
    {
        using var temp = new TempRoot();

        ConfigurationStore.Save(temp.ConfigPath, WithPasswords(NoPassword));

        await Assert.That(File.Exists(temp.SecretsPath)).IsFalse();
        await Assert.That(File.ReadAllText(temp.ConfigPath)).DoesNotContain("passwordRef");
        await Assert.That(Reload(temp.ConfigPath).Password).IsNull();
        await Assert.That(Reload(temp.ConfigPath).PasswordRef).IsNull();
    }

    /// <summary>
    /// Blanking the last password takes the file with it, and clears the reference. The empty string counts
    /// as blank here — the F5 field commits null for a blank, but the store must not leave a row keyed to
    /// <c>""</c> if anything ever hands it one.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task ClearingThePasswordRemovesTheRowTheReferenceAndTheFile(string? blank)
    {
        using var temp = new TempRoot();
        var config = WithPasswords("zvxq-forget-71");

        ConfigurationStore.Save(temp.ConfigPath, config);
        await Assert.That(File.Exists(temp.SecretsPath)).IsTrue();

        config.Worlds[0].Characters[0].Password = blank;
        ConfigurationStore.Save(temp.ConfigPath, config);

        await Assert.That(config.Worlds[0].Characters[0].PasswordRef).IsNull();
        await Assert.That(File.Exists(temp.SecretsPath)).IsFalse();
        await Assert.That(Reload(temp.ConfigPath).Password).IsNull();
    }

    /// <summary>
    /// Deleting a character deletes its password. This is the orphan question, and the answer is structural
    /// rather than a sweep: the secrets file is rewritten from the characters that exist, so a row nothing
    /// references cannot survive a save. The surviving character keeps its own row and its own value.
    /// </summary>
    [Test]
    public async Task DeletingACharacterTakesItsStoredPasswordWithIt()
    {
        using var temp = new TempRoot();
        var config = WithPasswords("zvxq-first-71", "zvxq-second-71");

        ConfigurationStore.Save(temp.ConfigPath, config);
        await Assert.That(SecretsStore.Read(temp.SecretsPath).Secrets.Count).IsEqualTo(2);

        config.Worlds[0].Characters.RemoveAt(0);
        ConfigurationStore.Save(temp.ConfigPath, config);

        var secrets = SecretsStore.Read(temp.SecretsPath).Secrets;
        await Assert.That(secrets.Count).IsEqualTo(1);
        await Assert.That(secrets.Values).DoesNotContain("zvxq-first-71");
        await Assert.That(File.ReadAllText(temp.SecretsPath)).DoesNotContain("zvxq-first-71");
        await Assert.That(Reload(temp.ConfigPath).Password).IsEqualTo("zvxq-second-71");
    }

    // ---- the duplicate gets its own row -------------------------------------------------------------

    /// <summary>
    /// A duplicated character gets its <b>own</b> row, so changing one password later does not change the
    /// other's. Two mechanisms agree on this and either alone would be enough:
    /// <see cref="CharacterDefinition.Clone"/> declines to copy the reference, and
    /// <c>ConfigurationStore.ReconcileSecrets</c> refuses to let two characters share a row whatever their
    /// references say. Both are exercised — the clone path here, the hand-edited-config path in
    /// <see cref="TwoCharactersHandedTheSameReferenceAreGivenSeparateRows"/>.
    /// </summary>
    [Test]
    public async Task ADuplicatedCharacterGetsItsOwnSecretsRow()
    {
        using var temp = new TempRoot();
        var config = WithPasswords("zvxq-shared-71");
        ConfigurationStore.Save(temp.ConfigPath, config);

        var copy = config.Worlds[0].Characters[0].Clone();
        copy.Name = "Corvid copy";
        config.Worlds[0].Characters.Add(copy);

        // Clone carries the value and not the reference — that is the declaration of intent.
        await Assert.That(copy.Password).IsEqualTo("zvxq-shared-71");
        await Assert.That(copy.PasswordRef).IsNull();

        ConfigurationStore.Save(temp.ConfigPath, config);

        var original = config.Worlds[0].Characters[0];
        await Assert.That(copy.PasswordRef).IsNotNull();
        await Assert.That(copy.PasswordRef).IsNotEqualTo(original.PasswordRef);
        await Assert.That(SecretsStore.Read(temp.SecretsPath).Secrets.Count).IsEqualTo(2);

        // And the point of separate rows: editing one leaves the other alone, across a real reload.
        original.Password = "zvxq-changed-71";
        ConfigurationStore.Save(temp.ConfigPath, config);

        var reloaded = ConfigurationStore.Load(temp.ConfigPath).Worlds[0].Characters;
        await Assert.That(reloaded[0].Password).IsEqualTo("zvxq-changed-71");
        await Assert.That(reloaded[1].Password).IsEqualTo("zvxq-shared-71");
    }

    /// <summary>
    /// The enforcement half: two characters carrying the same reference — reachable through a hand-edited
    /// config, or one copied between machines — are given separate rows on the next save rather than left
    /// sharing one. Sharing would mean editing one password silently changed the other, invisibly, behind
    /// two masks.
    /// </summary>
    [Test]
    public async Task TwoCharactersHandedTheSameReferenceAreGivenSeparateRows()
    {
        using var temp = new TempRoot();
        var config = WithPasswords("zvxq-one-71", "zvxq-two-71");
        var shared = Guid.NewGuid();
        config.Worlds[0].Characters[0].PasswordRef = shared;
        config.Worlds[0].Characters[1].PasswordRef = shared;

        ConfigurationStore.Save(temp.ConfigPath, config);

        var characters = config.Worlds[0].Characters;
        await Assert.That(characters[0].PasswordRef).IsNotEqualTo(characters[1].PasswordRef);

        var reloaded = ConfigurationStore.Load(temp.ConfigPath).Worlds[0].Characters;
        await Assert.That(reloaded[0].Password).IsEqualTo("zvxq-one-71");
        await Assert.That(reloaded[1].Password).IsEqualTo("zvxq-two-71");
    }

    // ---- the connect line resolves from the loaded value --------------------------------------------

    /// <summary>
    /// The tokens still work, and now they work off a password that came from the secrets file rather than
    /// from the keyboard. This is the end-to-end shape of the feature — saved password plus saved template
    /// equals a working auto-login after a restart — and it is why the tokens were kept: the config holds
    /// <c>connect %CHARACTER% %PASSWORD%</c>, which is safe to share, and the substitution happens at send
    /// time.
    /// </summary>
    [Test]
    public async Task TheConnectLineResolvesFromALoadedPassword()
    {
        using var temp = new TempRoot();
        const string secret = "zvxq-token-71";

        ConfigurationStore.Save(temp.ConfigPath, WithPasswords(secret));
        var character = Reload(temp.ConfigPath);

        // The default template, which the file stores as null — so this is also the no-migration case.
        await Assert.That(character.ConnectString).IsNull();
        await Assert.That(character.ResolveConnectString()).IsEqualTo($"connect Corvid {secret}");

        // The resolved line is nowhere on disk; only the template is.
        await Assert.That(File.ReadAllText(temp.ConfigPath)).DoesNotContain($"connect Corvid {secret}");
    }

    /// <summary>
    /// A custom template resolves the same way, and a stored password containing the token's own text is
    /// inserted verbatim rather than rescanned — the one-pass rule, asserted against a value that came off
    /// disk. A secret cannot be reinterpreted as syntax by making a trip through the files.
    /// </summary>
    [Test]
    public async Task AStoredPasswordIsNeverRescannedAsTemplateSyntax()
    {
        using var temp = new TempRoot();
        var config = WithPasswords("%CHARACTER%");
        config.Worlds[0].Characters[0].ConnectString = "co %CHARACTER% %PASSWORD%";

        ConfigurationStore.Save(temp.ConfigPath, config);

        await Assert.That(Reload(temp.ConfigPath).ResolveConnectString()).IsEqualTo("co Corvid %CHARACTER%");
    }

    // ---- everything degrades to "no password" -------------------------------------------------------

    /// <summary>
    /// The degradation contract, at each way it can be reached: a reference with no matching row, a missing
    /// secrets file, a file that is not JSON, a file that is JSON but not an object, and a file whose keys
    /// are not GUIDs. Every one of them means "no password" — the config still loads, the character still
    /// exists, and <c>%PASSWORD%</c> resolves to nothing so the login line is <c>connect Corvid</c> rather
    /// than a line with a hole in it.
    /// <para>
    /// None of these is allowed to throw. A client that refuses to start because of a permission bit on a
    /// convenience file is worse than one that asks for the password again.
    /// </para>
    /// </summary>
    [Test]
    [Arguments(null, "the secrets file is missing")]
    [Arguments("", "the secrets file is empty")]
    [Arguments("{}", "the secrets file has no rows")]
    [Arguments("not json at all", "the secrets file is not JSON")]
    [Arguments("[1, 2, 3]", "the secrets file is not an object")]
    [Arguments("\"just a string\"", "the secrets file is a bare JSON value")]
    [Arguments("{ \"not-a-guid\": \"zvxq\" }", "the key is not a GUID")]
    [Arguments("{ \"00000000-0000-0000-0000-000000000001\": 42 }", "the value is not text")]
    public async Task ABrokenSecretsFileMeansNoPasswordAndNeverAnError(string? secretsContent, string why)
    {
        using var temp = new TempRoot();
        ConfigurationStore.Save(temp.ConfigPath, WithPasswords("zvxq-degrade-71"));

        if (secretsContent is null)
        {
            File.Delete(temp.SecretsPath);
        }
        else
        {
            File.WriteAllText(temp.SecretsPath, secretsContent);
        }

        var config = ConfigurationStore.Load(temp.ConfigPath);
        var character = config.Worlds[0].Characters[0];

        await Assert.That(character.Password).IsNull().Because(why);

        // The reference is *kept*, not dropped: a file that is fixed later is picked back up, and a save in
        // between does not mint a new GUID for a row that was fine all along.
        await Assert.That(character.PasswordRef).IsNotNull().Because(why);

        // And the login line is still a valid line, with no dangling space where the token was.
        await Assert.That(character.ResolveConnectString()).IsEqualTo("connect Corvid").Because(why);
    }

    /// <summary>
    /// A fixed secrets file is picked straight back up, which is the payoff for keeping the reference through
    /// a failed read. This is the recovery path stated as a test rather than as a comment.
    /// </summary>
    [Test]
    public async Task AReferenceOutlivesABrokenSecretsFileAndResolvesAgainOnceItIsFixed()
    {
        using var temp = new TempRoot();
        var config = WithPasswords("zvxq-recover-71");
        ConfigurationStore.Save(temp.ConfigPath, config);
        var reference = config.Worlds[0].Characters[0].PasswordRef!.Value;

        var good = File.ReadAllText(temp.SecretsPath);
        File.WriteAllText(temp.SecretsPath, "corrupt");
        await Assert.That(Reload(temp.ConfigPath).Password).IsNull();

        File.WriteAllText(temp.SecretsPath, good);
        var recovered = Reload(temp.ConfigPath);
        await Assert.That(recovered.PasswordRef).IsEqualTo(reference);
        await Assert.That(recovered.Password).IsEqualTo("zvxq-recover-71");
    }

    /// <summary>
    /// An unreadable file is reported to <c>Load</c>'s callback <b>once</b>, and a normal one says nothing at
    /// all. That asymmetry is the whole point: "you have no saved passwords" is the ordinary state of most
    /// users and must not produce a notice, while "there is something stored here and it could not be read"
    /// is worth one line in the client message log.
    /// </summary>
    [Test]
    public async Task AnUnreadableSecretsFileIsReportedExactlyOnceAndAGoodOneIsSilent()
    {
        using var temp = new TempRoot();
        ConfigurationStore.Save(temp.ConfigPath, WithPasswords("zvxq-report-71", "zvxq-report-72"));

        var quiet = new List<string>();
        ConfigurationStore.Load(temp.ConfigPath, quiet.Add);
        await Assert.That(quiet).IsEmpty();

        File.WriteAllText(temp.SecretsPath, "corrupt");
        var noisy = new List<string>();
        ConfigurationStore.Load(temp.ConfigPath, noisy.Add);

        // One line for the file, not one per character — there are two characters affected.
        await Assert.That(noisy.Count).IsEqualTo(1);
        await Assert.That(noisy[0]).IsNotEmpty();

        // And no secret in the notice: it goes to a log the user can read and paste.
        await Assert.That(noisy[0]).DoesNotContain("zvxq-report-71");
    }

    /// <summary>
    /// A missing secrets file is not a problem to report. Most users are in this state.
    /// </summary>
    [Test]
    public async Task AMissingSecretsFileIsNotReported()
    {
        using var temp = new TempRoot();
        ConfigurationStore.Save(temp.ConfigPath, WithPasswords(NoPassword));

        var notices = new List<string>();
        ConfigurationStore.Load(temp.ConfigPath, notices.Add);

        await Assert.That(notices).IsEmpty();
        await Assert.That(SecretsStore.Read(temp.SecretsPath).Readable).IsTrue();
    }

    /// <summary>
    /// An unreadable secrets file is <b>moved aside</b>, not destroyed, by the save that replaces it. This is
    /// the one data-loss path the design has, and this is how it is closed: the client cannot have loaded
    /// those passwords, so it is about to write a map that demonstrably does not contain them, and the bytes
    /// are the user's only copy. They end up in <c>secrets.json.unreadable</c> and the save still succeeds —
    /// because refusing to save would mean a newly typed password did not stick, which is the complaint that
    /// starts the whole cycle again.
    /// </summary>
    [Test]
    public async Task AnUnreadableSecretsFileIsMovedAsideRatherThanOverwritten()
    {
        using var temp = new TempRoot();
        var config = WithPasswords("zvxq-aside-71");
        ConfigurationStore.Save(temp.ConfigPath, config);

        const string corrupt = "{ this is not json and holds someone's only copy";
        File.WriteAllText(temp.SecretsPath, corrupt);

        // The load that finds it broken, then an ordinary save — the shape of "open F5 and toggle something".
        var loaded = ConfigurationStore.Load(temp.ConfigPath);
        ConfigurationStore.Save(temp.ConfigPath, loaded);

        var aside = temp.SecretsPath + ".unreadable";
        await Assert.That(File.Exists(aside)).IsTrue();
        await Assert.That(File.ReadAllText(aside)).IsEqualTo(corrupt);
    }

    // ---- the file mode ------------------------------------------------------------------------------

    /// <summary>
    /// The secrets file is readable and writable by its owner and by nobody else — the mitigation that makes
    /// plaintext defensible at all.
    /// <para>
    /// Skipped on Windows, where <see cref="SecretsStore.RestrictToOwner"/> deliberately does nothing: the
    /// file inherits <c>%APPDATA%</c>'s user-only ACL and hand-rolling a DACL no CI here can exercise would
    /// be untested security code. That is a documented decision, not an oversight, so the test states it
    /// rather than pretending the platform is uninteresting.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheSecretsFileIsReadableOnlyByItsOwner()
    {
        using var temp = new TempRoot();

        ConfigurationStore.Save(temp.ConfigPath, WithPasswords("zvxq-mode-71"));

        if (OperatingSystem.IsWindows())
        {
            await Assert.That(SecretsStore.RestrictToOwner(temp.SecretsPath)).IsFalse();
            return;
        }

        var mode = File.GetUnixFileMode(temp.SecretsPath);
        await Assert.That(mode).IsEqualTo(SecretsStore.OwnerOnlyMode);

        // Spelled out bit by bit against the file rather than against the constant: the equality above would
        // still hold if OwnerOnlyMode itself were widened, and the point is that group and other read nothing.
        await Assert.That(mode.HasFlag(UnixFileMode.UserRead)).IsTrue();
        await Assert.That(mode.HasFlag(UnixFileMode.UserWrite)).IsTrue();
        foreach (var forbidden in new[]
        {
            UnixFileMode.UserExecute,
            UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute,
            UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute,
        })
        {
            await Assert.That(mode.HasFlag(forbidden)).IsFalse().Because(forbidden.ToString());
        }
    }

    /// <summary>
    /// An existing over-permissive secrets file is tightened on the next save. Reachable through an
    /// over-broad umask, a hand copy, or a restore from an archive that did not preserve modes — and
    /// tightening it happens without asking, because the condition is already fixed by the time anyone could
    /// be told and refusing to write would lose passwords over a permission bit.
    /// </summary>
    [Test]
    public async Task AnExistingOverPermissiveSecretsFileIsTightenedOnSave()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempRoot();
        var config = WithPasswords("zvxq-tighten-71");
        ConfigurationStore.Save(temp.ConfigPath, config);

        const UnixFileMode wide = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                  UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(temp.SecretsPath, wide);
        await Assert.That(File.GetUnixFileMode(temp.SecretsPath)).IsEqualTo(wide);

        config.Worlds[0].Characters[0].Password = "zvxq-tighten-72";
        ConfigurationStore.Save(temp.ConfigPath, config);

        await Assert.That(File.GetUnixFileMode(temp.SecretsPath)).IsEqualTo(SecretsStore.OwnerOnlyMode);

        // Tightened *and* written: a mode fix that lost the save would be the worse bug.
        await Assert.That(Reload(temp.ConfigPath).Password).IsEqualTo("zvxq-tighten-72");
    }

    /// <summary>
    /// The mode is in place before any content is. A chmod that followed the write would leave a window in
    /// which a brand-new world-readable file already held a password; the file is created through
    /// <c>FileStreamOptions.UnixCreateMode</c> instead, so no such window exists and the umask never gets a
    /// say.
    /// </summary>
    [Test]
    public async Task TheModeIsAppliedByTheOpenThatCreatesTheFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempRoot();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.SecretsPath)!);

        await Assert.That(File.Exists(temp.SecretsPath)).IsFalse();
        await Assert.That(SecretsStore.RestrictToOwner(temp.SecretsPath)).IsTrue();

        await Assert.That(File.Exists(temp.SecretsPath)).IsTrue();
        await Assert.That(new FileInfo(temp.SecretsPath).Length).IsEqualTo(0);
        await Assert.That(File.GetUnixFileMode(temp.SecretsPath)).IsEqualTo(SecretsStore.OwnerOnlyMode);
    }

    /// <summary>
    /// <c>config.json</c>'s own mode is left exactly as found — it holds no secrets, it is the file this
    /// design exists to make shareable, and narrowing a file the user hand-edits and pastes would buy
    /// nothing. Pinned so a later "harden everything" change has to argue with something.
    /// </summary>
    [Test]
    public async Task TheConfigFilesOwnModeIsLeftAlone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempRoot();
        var config = WithPasswords("zvxq-configmode-71");
        ConfigurationStore.Save(temp.ConfigPath, config);

        const UnixFileMode readable = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                      UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(temp.ConfigPath, readable);

        config.Worlds[0].Characters[0].Password = "zvxq-configmode-72";
        ConfigurationStore.Save(temp.ConfigPath, config);

        await Assert.That(File.GetUnixFileMode(temp.ConfigPath)).IsEqualTo(readable);
        await Assert.That(File.GetUnixFileMode(temp.SecretsPath)).IsEqualTo(SecretsStore.OwnerOnlyMode);
    }

    /// <summary>
    /// The secrets file goes beside the config and nowhere else, whatever the config path looks like.
    /// </summary>
    [Test]
    public async Task TheSecretsFileSitsBesideTheConfiguration()
    {
        using var temp = new TempRoot();

        ConfigurationStore.Save(temp.ConfigPath, WithPasswords("zvxq-where-71"));

        await Assert.That(SecretsStore.PathFor(temp.ConfigPath)).IsEqualTo(temp.SecretsPath);
        await Assert.That(File.Exists(temp.SecretsPath)).IsTrue();

        // The name matters beyond tidiness: `.gitignore` already ignores `secrets.json`, so a user keeping
        // their config directory in a dotfiles repository is protected without having to know this design
        // exists. Asserted against the file that was actually created rather than against the constant.
        await Assert.That(Path.GetFileName(temp.SecretsPath)).IsEqualTo("secrets.json");
    }

    // ---- migration ---------------------------------------------------------------------------------

    /// <summary>
    /// Nothing was migrated, and nothing needed to be. An existing v2 document has no <c>passwordRef</c>,
    /// which deserializes to null and means "no stored password" — the state the user was already in, since
    /// <see cref="CharacterDefinition.Password"/> was never serialized and so nothing exists on disk to
    /// convert. The secrets split contributed no migration step of its own: versions exist for renames
    /// and restructures, not for an additive optional field.
    /// <para>
    /// Asserted against a raw stored document rather than a round-trip, the way the connect-string default's
    /// own no-migration test is, because the thing being pinned is what happens to bytes somebody already
    /// has.
    /// </para>
    /// <para>
    /// The version this comes back as is no longer 2, and that is not this claim weakening. v3 changed
    /// what a world's <c>encoding</c> <em>means</em> — a preference became an override, with
    /// <c>auto</c> as the new default — so the migrator now has a step that moves this document's
    /// version and touches nothing else in it. The claim is therefore restated where it belongs:
    /// every character field is exactly as stored, no secret appears, and the world that named no
    /// encoding lands on <c>auto</c> rather than being pinned to anything.
    /// </para>
    /// </summary>
    [Test]
    public async Task AnExistingConfigNeedsNoMigrationForTheSecretsSplit()
    {
        const string stored = """
        {
          "version": 2,
          "worlds": [
            {
              "name": "Aetherfall",
              "host": "aetherfall.mux",
              "characters": [ { "name": "Corvid", "autoLogin": true } ]
            }
          ]
        }
        """;

        using var temp = new TempRoot();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.ConfigPath)!);
        File.WriteAllText(temp.ConfigPath, stored);

        var notices = new List<string>();
        var config = ConfigurationStore.Load(temp.ConfigPath, notices.Add);
        var character = config.Worlds[0].Characters[0];

        // Brought to the current version by the v3 encoding rename and by nothing else: no character
        // field moved, and the world that named no encoding reads `auto` — the new default — rather
        // than being pinned to whatever the old default happened to be.
        await Assert.That(config.Version).IsEqualTo(AppConfiguration.CurrentVersion);
        await Assert.That(config.Worlds[0].Encoding).IsEqualTo("auto");
        await Assert.That(character.Name).IsEqualTo("Corvid");
        await Assert.That(character.AutoLogin).IsTrue();
        await Assert.That(character.PasswordRef).IsNull();
        await Assert.That(character.Password).IsNull();
        await Assert.That(character.ResolveConnectString()).IsEqualTo("connect Corvid");

        // No secrets file was created by merely reading, and nothing was reported about its absence.
        await Assert.That(File.Exists(temp.SecretsPath)).IsFalse();
        await Assert.That(notices).IsEmpty();

        // And typing a password for the first time is an ordinary save from there.
        character.Password = "zvxq-migrated-71";
        ConfigurationStore.Save(temp.ConfigPath, config);
        await Assert.That(Reload(temp.ConfigPath).Password).IsEqualTo("zvxq-migrated-71");
    }
}
