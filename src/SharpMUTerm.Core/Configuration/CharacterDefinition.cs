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
    /// Login password, in memory. It <b>is</b> persisted — but not into <c>config.json</c>, which is why
    /// this property is <c>[JsonIgnore]</c> and <see cref="PasswordRef"/> exists. The config stores a GUID;
    /// <see cref="SecretsStore"/> stores <c>GUID → password</c> in a separate owner-only
    /// <c>secrets.json</c>, and <see cref="ConfigurationStore"/> joins the two on load and splits them
    /// again on save.
    /// <para>
    /// The <c>[JsonIgnore]</c> here is not the old "passwords are never saved" design, which it outlived —
    /// it is the mechanism of the new one. Two earlier designs were considered and are worth knowing about,
    /// because both are things a future change might drift back into. <b>Session-only</b> meant everybody
    /// retyped their password, and the workaround people reach for is baking it into
    /// <see cref="ConnectString"/>, which was serialized anyway — so it did not keep secrets off disk, it
    /// only kept them out of the field built to hold them. <b>Plaintext in config.json</b> saved the
    /// password honestly and leaked it the first time anyone pasted their config into a help channel, which
    /// is a thing MU* users do constantly and a thing that has already happened here. Splitting the files
    /// fixes the leak that actually occurs without pretending to fix the one it does not: the value is still
    /// plaintext, and <see cref="SecretsStore"/> says so in as many words.
    /// </para>
    /// <para>
    /// It is typed into the F5 form and joined to the login line by
    /// <see cref="ConnectStringTemplate.PasswordToken"/> at send time. The token is still the right design:
    /// it keeps the secret in one masked field instead of duplicated into a connect line that is drawn in
    /// the clear, and substitution at send time is what keeps the resolved line off the echo, the transcript
    /// and the history list. What has changed is only that the substituted value now survives a restart.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string? Password { get; set; }

    /// <summary>
    /// Which row of <see cref="SecretsStore"/> holds this character's <see cref="Password"/>, or null when
    /// it has none. This is the one password-related thing that reaches <c>config.json</c>, and it is
    /// deliberately a GUID: it carries no information at all, so a shared config discloses nothing beyond
    /// the fact that <em>a</em> password exists.
    /// <para>
    /// Nothing outside <see cref="ConfigurationStore"/> should set it. Saving reconciles it against
    /// <see cref="Password"/> — allocating one for a character that has a password and no reference,
    /// clearing it for a character whose password was blanked — so the invariant "a reference exists exactly
    /// when a stored password does" is maintained in one place rather than by every caller remembering to.
    /// </para>
    /// <para>
    /// A reference with no matching row resolves to <b>no password</b>, not to an error: see
    /// <see cref="SecretsStore"/> for why every failure on that path degrades instead of blocking a login.
    /// </para>
    /// </summary>
    public Guid? PasswordRef { get; set; }

    /// <summary>
    /// The login line to send, as a template — <c>connect %CHARACTER% %PASSWORD%</c> by default (see
    /// <see cref="ConnectStringTemplate"/> for the token, escaping and empty-value rules). Null means
    /// "use the default", which is why no config migration was needed when the default stopped being
    /// hand-built and became a template: an existing character with <c>connectString: null</c> resolves
    /// to the very same line it always did, and one carrying its own line keeps it.
    /// </summary>
    public string? ConnectString { get; set; }

    /// <summary>
    /// Connect this character when the client starts. Zero, one or several characters may be marked, on
    /// any number of worlds; each gets a session at launch, the first in configuration order taking the
    /// main window and the rest a tab apiece.
    /// <para>
    /// <b>Not the login.</b> This says <em>whether to open the socket at all, unasked</em>; what gets
    /// typed once one is open is <see cref="Login"/>'s question, answered from
    /// <see cref="Password"/> and <see cref="ConnectString"/>. The two remain independent in both
    /// directions, and that has not changed: marking this on a character with no password and no connect
    /// line dials you to the login screen and leaves you to log in by hand, and a character that logs
    /// itself in whenever <em>you</em> dial needs nothing marked here.
    /// <para>
    /// What <em>did</em> change is that the login half is no longer a second boolean beside this one. It
    /// was <c>autoLogin</c>, and requiring it in addition to a saved password meant this exact
    /// configuration — marked here, password saved, that flag at its default — connected and then typed
    /// nothing, silently. See <see cref="LoginPlan"/> for the argument; the point here is only that
    /// there is one flag on this object about connecting, not two that have to agree.
    /// </para>
    /// <para>
    /// It defaults to <c>false</c>, and there is deliberately no migration marking anybody. Until this
    /// existed the client connected the first configured world's first character on every launch, with
    /// no way to say which or to say no; a migration that reinstated that would re-impose on precisely
    /// the users who never chose it. An upgraded config therefore starts connected to nothing, and the
    /// client says so and names the two keys that change it (see <c>SharpMUTermApp.StartAsync</c>).
    /// </para>
    /// <para>
    /// It lives on the character rather than as a pointer from the world — <c>WorldDefinition</c> naming
    /// a character it should connect — because a name in one object referring to a row in another goes
    /// stale the moment that row is renamed or deleted, and a dangling reference that silently connects
    /// nothing is the failure mode this codebase has already had to build reports for elsewhere
    /// (<c>TriggerSetReferences</c>). A boolean on the thing it describes cannot dangle: rename the
    /// character and the mark travels with it, delete the character and the mark goes too.
    /// </para>
    /// </summary>
    public bool ConnectAtStartup { get; set; }

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
    /// <see cref="Password"/> is still carried over, but the old justification for that — "safe, because
    /// nothing here reaches disk" — is void, and this is the replacement. A duplicate is a copy of a
    /// character, and the password is one of the things that makes a character connectable; a duplicate that
    /// dropped it would look complete on screen (the mask cannot distinguish "copied" from "cleared") and
    /// then fail to log in, which is the failure a user cannot diagnose from the form. The way to have a copy
    /// without the credential is to blank the field, which is one keystroke and visible on the row.
    /// </para>
    /// <para>
    /// <see cref="ConnectAtStartup"/> is carried over for the same reason as everything else here, and
    /// unlike the password it needs no argument about hidden state: the duplicate draws the mark on its
    /// own F5 row, so a copy that will dial at launch says so where the copy is made. Dropping it would
    /// be the quieter surprise — a duplicate of an auto-connecting character that silently does not.
    /// </para>
    /// <para>
    /// <b><see cref="PasswordRef"/> is deliberately <em>not</em> copied.</b> The copy gets its own row in
    /// <see cref="SecretsStore"/> — the next save allocates one, because that is what a null reference beside
    /// a set password means. Sharing a row would mean editing one character's password silently changed the
    /// other's, which is a surprise nobody asked for and one that would be invisible behind two masks.
    /// <see cref="ConfigurationStore"/> refuses to let two characters share a row anyway, so this is the
    /// declaration of intent and that is the enforcement; they agree, and either alone would be enough.
    /// </para>
    /// </summary>
    public CharacterDefinition Clone() => new()
    {
        Name = Name,
        Password = Password,
        PasswordRef = null,
        ConnectString = ConnectString,
        ConnectAtStartup = ConnectAtStartup,
        OnConnect = OnConnect,
        OnDisconnect = OnDisconnect,
        TriggerSets = new List<string>(TriggerSets),
        Logging = new LoggingSettings
        {
            Format = Logging.Format,
            Directory = Logging.Directory,
            RestoreLog = Logging.RestoreLog,
        },
    };

    /// <summary>
    /// The login line to send: this character's <see cref="ConnectString"/> — or
    /// <see cref="ConnectStringTemplate.Default"/> when it has none — with its tokens substituted. The
    /// secret is joined to the line here, at the last possible moment, and nowhere else.
    /// </summary>
    public string ResolveConnectString() => ConnectStringTemplate.Resolve(ConnectString, Name, Password);

    /// <summary>
    /// What this character will do at a login prompt, and why — the whole rule, in one place, derived
    /// from the fields the user filled in rather than from a separate switch they also had to find.
    /// <para>
    /// <b>The rule: a character logs itself in when its configuration says what to send.</b> A saved
    /// <see cref="Password"/> is one such statement; a <see cref="ConnectString"/> the user wrote is the
    /// other. Neither is a second-order preference about the first — either one, on its own, is as
    /// unambiguous as a configuration gets, and the way to say "leave the login to me" is to fill in
    /// neither. See <see cref="LoginPlan"/> for what this replaced and why.
    /// </para>
    /// <para>
    /// A line that resolves to nothing counts as nothing: <c>%PASSWORD%</c> alone with no password set is
    /// an empty template, and a bare newline at a login prompt is not "no command" — it is a command some
    /// servers answer to.
    /// </para>
    /// </summary>
    public LoginPlan Login()
    {
        var hasPassword = !string.IsNullOrEmpty(Password);
        var hasOwnLine = !string.IsNullOrWhiteSpace(ConnectString);

        if ((!hasPassword && !hasOwnLine) || string.IsNullOrWhiteSpace(ResolveConnectString()))
        {
            return LoginPlan.Nothing;
        }

        if (!hasPassword)
        {
            return LoginPlan.WithoutPassword;
        }

        return ConnectStringTemplate.UsesPassword(ConnectString)
            ? LoginPlan.WithPassword
            : LoginPlan.PasswordUnused;
    }
}
