using System.Text;

namespace SharpMUTerm.Core.Configuration;

/// <summary>
/// The login line as a <em>template</em>: <c>connect %CHARACTER% %PASSWORD%</c>, with the values
/// substituted at send time rather than stored in the line.
/// <para>
/// This exists for one reason, and it is a security reason rather than a convenience one.
/// <see cref="CharacterDefinition.Password"/> is <c>[JsonIgnore]</c> and never reaches disk, while
/// <see cref="CharacterDefinition.ConnectString"/> <em>is</em> serialized — so before tokens, the only
/// way to persist a working login line was to bake the password into it in plaintext. A template
/// persists safely and the secret is joined to it only on the way to the socket, which is strictly
/// better than the trade-off it replaces. It also gives a user somewhere to put a password other than
/// the command line: typed input is echoed locally and written to the session transcript
/// (<c>WorldSession.SendUserInputAsync</c> → <c>Print</c>), while the auto-login line goes straight out
/// through <c>SendRawAsync</c> with no echo and no log write.
/// </para>
/// <para>
/// The rules, all four of them decided here so every caller and every test reads the same ones:
/// </para>
/// <list type="bullet">
/// <item><b>Delimiters.</b> <c>%NAME%</c>, both ends. One spelling, so a line cannot half-look like a
/// token.</item>
/// <item><b>Case.</b> Token names are matched case-insensitively (<c>%Password%</c> works), the same way
/// trigger-set names and every field choice in this app are matched. The canonical spelling is upper
/// case and that is what the default and the UI show. A token the user plainly meant, left unrecognised,
/// would be sent to the server as literal text and invite them to "fix" it by pasting the real password
/// into the line — which is the thing this type exists to stop.</item>
/// <item><b>Escaping.</b> <c>%%</c> is a literal <c>%</c>. So <c>%%PASSWORD%%</c> sends the seven
/// characters <c>%PASSWORD%</c> and substitutes nothing.</item>
/// <item><b>Unknown tokens are left verbatim.</b> <c>%PASWORD%</c> is sent as typed. It is the
/// conservative rule — only what is recognised is rewritten — and the visible one: the server refuses
/// the login and the line that did it is still readable. Stripping it would send a login line silently
/// missing its password, which on many servers connects you as a guest instead of failing.</item>
/// </list>
/// <para>
/// <b>Empty values</b> get one narrow rule: a token that resolves to nothing also takes one adjacent
/// space with it — the space before it if there is one, else the space after — so
/// <c>connect %CHARACTER% %PASSWORD%</c> with no password sends <c>connect Corvid</c> with no dangling
/// space, exactly as the old hand-built line did. Nothing else is collapsed and the result is not
/// trimmed, because a template with no tokens in it must come back byte-identical: people have working
/// configs, and a whitespace rule applied to the whole line would quietly rewrite them.
/// </para>
/// <para>
/// A substituted value is <b>never rescanned</b>: a password of <c>50%%off</c> or even
/// <c>%CHARACTER%</c> is inserted as it stands. Substitution is one left-to-right pass over the
/// template, so a secret can never be reinterpreted as syntax.
/// </para>
/// <para>
/// Only the two tokens <see cref="CharacterDefinition"/> can answer for itself are defined.
/// <c>%WORLD%</c>/<c>%HOST%</c>/<c>%PORT%</c> were considered and deliberately left out: a character
/// does not know its parent world, so they would need threading through a signature with one caller,
/// and any caller that could not supply a world would emit the token verbatim to the server. A token
/// that resolves at one call site and leaks its own name at another is a worse contract than a token
/// that does not exist. If a world-level token is ever wanted, the shape is a resolution context the
/// session fills in — not an optional parameter.
/// </para>
/// </summary>
public static class ConnectStringTemplate
{
    /// <summary>The delimiter a token opens and closes with, and the character <c>%%</c> escapes.</summary>
    public const char Delimiter = '%';

    /// <summary>The character's name, as configured.</summary>
    public const string CharacterToken = "%CHARACTER%";

    /// <summary>The session-only password, or nothing at all when none is set.</summary>
    public const string PasswordToken = "%PASSWORD%";

    /// <summary>
    /// The login line used when a character has no <see cref="CharacterDefinition.ConnectString"/> of
    /// its own. It is a literal template rather than interpolated C# so the F5 screen can <em>show</em>
    /// it, and so editing it starts from something that already works.
    /// </summary>
    public const string Default = "connect " + CharacterToken + " " + PasswordToken;

    /// <summary>Every token this resolver knows, in the order the default uses them.</summary>
    public static IReadOnlyList<string> Tokens { get; } = new[] { CharacterToken, PasswordToken };

    /// <summary>
    /// Substitutes <paramref name="character"/> and <paramref name="password"/> into
    /// <paramref name="template"/>, falling back to <see cref="Default"/> when the template is null or
    /// blank. See the type summary for the escaping, case, unknown-token and empty-value rules.
    /// </summary>
    public static string Resolve(string? template, string character, string? password)
    {
        ArgumentNullException.ThrowIfNull(character);

        var source = string.IsNullOrWhiteSpace(template) ? Default : template!;
        var result = new StringBuilder(source.Length + character.Length + (password?.Length ?? 0));

        for (var i = 0; i < source.Length;)
        {
            if (source[i] != Delimiter)
            {
                result.Append(source[i++]);
                continue;
            }

            // %% is a literal per cent, and is checked first so an escape can never be read as the
            // opening delimiter of a token.
            if (i + 1 < source.Length && source[i + 1] == Delimiter)
            {
                result.Append(Delimiter);
                i += 2;
                continue;
            }

            var close = source.IndexOf(Delimiter, i + 1);
            if (close < 0 || Value(source[(i + 1)..close], character, password) is not { } value)
            {
                // An unterminated or unrecognised token is text. Only the delimiter is consumed, so the
                // rest of it is rescanned as ordinary characters and comes out exactly as it went in.
                result.Append(source[i++]);
                continue;
            }

            i = close + 1;
            if (value.Length > 0)
            {
                result.Append(value);
                continue;
            }

            // The token resolved to nothing: drop the single space that was holding it apart from its
            // neighbours, before it if there is one, else after it.
            if (result.Length > 0 && result[^1] == ' ')
            {
                result.Length--;
            }
            else if (i < source.Length && source[i] == ' ')
            {
                i++;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// What a token name resolves to, or null when this resolver does not know it — which is what tells
    /// "resolved to nothing" (an unset password) apart from "not a token at all" (a typo). The two must
    /// not be one answer: the first drops a space, the second is copied through verbatim.
    /// </summary>
    private static string? Value(string name, string character, string? password)
    {
        if (Named(name, CharacterToken))
        {
            return character;
        }

        return Named(name, PasswordToken) ? password ?? string.Empty : null;
    }

    /// <summary>Whether an undelimited name is <paramref name="token"/>, ignoring case.</summary>
    private static bool Named(string name, string token) =>
        string.Equals(name, token.Trim(Delimiter), StringComparison.OrdinalIgnoreCase);
}
