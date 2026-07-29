namespace SharpMUTerm.Core.Input;

/// <summary>
/// The rule that keeps typed passwords out of <see cref="InputHistory"/>: a login line carries a
/// plaintext password, and history is the one place in this client that would keep it.
/// <para>
/// A configured character never comes through here — <c>WorldSession.SendLoginAsync</c> writes its
/// connect string straight to the transport and never touches the UI's history. The line this exists for
/// is the one a user types by hand on a world they have not configured yet: <c>connect Corvid hunter2</c>.
/// That was always a latent hazard, recoverable one entry at a time with <c>↑</c>; a browsable, searchable
/// history surface is what turns it into a real one, so the rule ships with the surface.
/// </para>
/// <para>
/// <b>Excluded, not masked.</b> A masked entry you cannot recall is useless — the whole point of history
/// is putting the line back on the command line — and a masked entry you <em>can</em> recall still holds
/// the secret, so masking buys nothing but the appearance of care. The line is simply never recorded.
/// </para>
/// <para>
/// <b>Deliberately over-inclusive.</b> The two failure modes are not symmetrical: wrongly dropping
/// <c>connect me here</c> costs one <c>↑</c> that does not work, while wrongly keeping
/// <c>connect Corvid hunter2</c> puts a password in a searchable list. So any login verb with something
/// that could be a password after the name is dropped, without trying to judge whether it looks like one.
/// </para>
/// <para>
/// <b>Nothing here is persisted.</b> History is in-memory for the life of the process and there is no
/// option to write it to disk — that would be a separate decision about secrets at rest.
/// </para>
/// </summary>
public static class HistorySecrets
{
    /// <summary>
    /// The MU* login verbs, which take <c>&lt;name&gt; &lt;password&gt;</c>. <c>cd</c> is connect-dark and
    /// <c>ch</c> connect-hidden (PennMUSH/TinyMUX wizard variants), and both take the same two arguments;
    /// <c>create</c> chooses a password rather than presenting one, which is no less a secret.
    /// </summary>
    private static readonly string[] LoginVerbs = { "connect", "cd", "ch", "create" };

    /// <summary>
    /// Verbs whose <em>first</em> argument is already the secret — <c>@password old=new</c>,
    /// <c>@newpassword player=pass</c>, <c>@pcreate name=pass</c>. One argument is enough to drop these,
    /// where a login verb needs two.
    /// </summary>
    private static readonly string[] PasswordVerbs = { "@password", "@newpassword", "@pcreate" };

    /// <summary>The verbs this rule knows, for a screen or a report that wants to name them.</summary>
    public static IReadOnlyList<string> Verbs { get; } =
        LoginVerbs.Concat(PasswordVerbs).ToArray();

    /// <summary>
    /// Whether a command line looks like it carries a password and so must not be recorded. Verb
    /// matching is case-insensitive, because MU* command dispatch is.
    /// </summary>
    public static bool LooksLikeCredential(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var words = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var verb = words[0];
        var arguments = words.Length - 1;

        // `connect Corvid` is a guest-style login with nothing to hide; `connect Corvid hunter2` is not.
        if (arguments >= 2 && LoginVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return arguments >= 1 && PasswordVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase);
    }
}
