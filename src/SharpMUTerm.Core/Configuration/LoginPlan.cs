namespace SharpMUTerm.Core.Configuration;

/// <summary>
/// What a character will actually do at its login prompt, derived from its own fields rather than
/// stored beside them — <see cref="CharacterDefinition.Login"/> is the one place that decides, and the
/// session, the F5 screen and every test read the same answer.
/// <para>
/// <b>It replaced a stored <c>autoLogin</c> boolean, and the reason is the bug it caused.</b> Sending
/// the connect line used to require that flag on <em>as well as</em> a saved password, so a character
/// with a password and the flag off saved a credential that did nothing at all — no login line, no
/// report, no way to tell that from a server ignoring one. That is not a setting a user can hold two
/// halves of in their head; it is a trap, and two of this repo's own characters were in it. Saving a
/// password (or writing a connect line) <em>is</em> the instruction to log in, and the way to say "I
/// will type it myself" is to save neither.
/// </para>
/// <para>
/// The three send-nothing/send-something states are not the whole answer, which is why this is an enum
/// and not a bool: a configuration can still be <em>inert</em> — see <see cref="PasswordUnused"/> — and
/// an inert configuration that says nothing is the same failure in a new shape. Every value here is
/// rendered on the F5 character form, and the two unhappy ones are also printed into the session at
/// connect time.
/// </para>
/// </summary>
public enum LoginPlan
{
    /// <summary>
    /// Nothing is sent. The character has no saved password and no connect line of its own — or has a
    /// connect line that resolves to nothing at all (<c>%PASSWORD%</c> alone, with no password). This is
    /// the deliberate "I log in by hand" state, and it is silent on purpose: a client that announced it
    /// on every connect would be nagging about a choice.
    /// </summary>
    Nothing,

    /// <summary>
    /// The connect line is sent with the saved password substituted into it — the ordinary case, and
    /// what the default template <see cref="ConnectStringTemplate.Default"/> does with a password set.
    /// </summary>
    WithPassword,

    /// <summary>
    /// The connect line is sent and no password is involved, because none is saved. Reached by writing a
    /// connect line of your own — <c>connect %CHARACTER%</c> on a passwordless world, or anything else a
    /// login prompt answers to. It is a first-class state rather than a degenerate one: "send only when
    /// a password exists" would have made a passwordless world unconfigurable.
    /// </summary>
    WithoutPassword,

    /// <summary>
    /// The connect line is sent, and the saved password is <b>not part of it</b> — the line has no
    /// <c>%PASSWORD%</c> token to put it in (or spells one that is escaped, or misspells it). The login
    /// goes out; the credential is dead weight.
    /// <para>
    /// This is the residue of the trap this enum exists to close, and it is the reason the answer is not
    /// a boolean: a stored secret that is never used has to be visible somewhere, or the user is back
    /// where they started — watching a login fail with no way to tell which half is missing. F5 says so
    /// on the character's own form, and the session says so in its window when it connects.
    /// </para>
    /// </summary>
    PasswordUnused,
}
